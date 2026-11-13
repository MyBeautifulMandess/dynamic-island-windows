using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>Данные сопряжения, которые попадают в QR-код.</summary>
public sealed record PhonePairingCode(string Address, int Port, string Token)
{
    /// <summary>Прямая ссылка на приложение — работает, когда камера открывает свои схемы.</summary>
    public string Url => $"dilink://v1?ip={Address}&port={Port}&token={Token}";

    /// <summary>
    /// Адрес страницы сопряжения. Именно он кладётся в QR: обычная камера
    /// открывает http-ссылку в браузере, а страница запускает приложение.
    /// </summary>
    public string WebUrl =>
        $"http://{Address}:{Port}{PhonePairingPage.Path}?ip={Address}&port={Port}&token={Token}";
}

/// <summary>
/// Общий транспорт для всех телефонных функций: сервер WebSocket на локальной
/// сети, сопряжение по QR, проверка ключа, ping/pong и рассылка сообщений.
/// Фичевые сервисы подписываются на <see cref="MessageReceived"/>, а не на окно.
/// </summary>
public sealed class PhoneLinkService : IDisposable
{
    private static readonly TimeSpan PairWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(30);

    private readonly PhoneSocketServer _server;
    private readonly List<PhoneSocketConnection> _connections = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Timer? _keepAlive;
    private Timer? _idleTimer;
    private DateTime _idleSinceUtc = DateTime.UtcNow;
    private string? _pairToken;
    private DateTime _pairExpiresAt;
    private int _port;
    private bool _disposed;

    /// <summary>Как долго держать порт открытым, когда телефон не подключён.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Выключатель автозакрытия: при false порт слушается постоянно.</summary>
    public bool IdleShutdownEnabled { get; set; } = true;

    /// <summary>Порт закрыт по простою: телефон не подключён слишком долго.</summary>
    public event Action? IdleClosed;

    public PhoneDeviceStore Devices { get; } = new();

    public event Action<string>? DeviceConnected;
    public event Action<string>? DeviceDisconnected;
    public event Action<string, PhoneEnvelope>? MessageReceived;
    public event Action<byte[]>? BinaryReceived;
    public event Action? ConnectionsChanged;

    public PhoneLinkService(int port = 19532)
    {
        _port = port;
        _server = new PhoneSocketServer(port);
        _server.Connected += OnClientConnected;
        _server.Disconnected += OnClientDisconnected;
    }

    public bool IsRunning => _server.IsRunning;

    public int Port => _port;

    /// <summary>Идентификаторы подключённых и авторизованных телефонов.</summary>
    public IReadOnlyList<string> ConnectedDevices
    {
        get { lock (_connections) return _connections.Where(x => x.IsAuthenticated).Select(x => x.DeviceId!).Distinct().ToArray(); }
    }

    public bool HasConnections => ConnectedDevices.Count > 0;

    public string LocalAddress => FindLocalAddress();

    public string Status { get; private set; } = "Выключено";

    public void Start(int port)
    {
        if (_disposed) return;
        Stop();
        _port = port;
        try
        {
            _server.Start();
            _cts = new CancellationTokenSource();
            _keepAlive = new Timer(_ => KeepAliveAsync(), null, PingInterval, PingInterval);
            _idleSinceUtc = DateTime.UtcNow;
            _idleTimer = new Timer(_ => CheckIdle(), null, IdleCheckInterval, IdleCheckInterval);
            Status = $"Ожидание телефона · {LocalAddress}:{port}";
            Diagnostics.Log($"PhoneLinkService: запущен на порту {port}, адрес {LocalAddress}");
        }
        catch (SocketException ex)
        {
            Status = $"Порт {port} занят";
            Diagnostics.Log($"PhoneLinkService: порт {port} недоступен — {ex.Message}");
            Stop();
        }
        catch (Exception ex)
        {
            Status = "Не удалось запустить сервер";
            Diagnostics.Log("PhoneLinkService: запуск не удался — " + ex.Message);
        }
    }

    public void Stop()
    {
        try { _keepAlive?.Dispose(); } catch { }
        _keepAlive = null;
        try { _idleTimer?.Dispose(); } catch { }
        _idleTimer = null;
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _server.Stop();
        lock (_connections)
        {
            foreach (var connection in _connections.ToArray()) connection.Close();
            _connections.Clear();
        }
        _pairToken = null;
        Status = "Выключено";
        ConnectionsChanged?.Invoke();
    }

    /// <summary>
    /// Выдаёт одноразовый код сопряжения для QR. Код живёт 10 минут и меняется
    /// при каждом показе, чтобы по старому снимку экрана нельзя было подключиться.
    /// </summary>
    public PhonePairingCode CreatePairingCode()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _pairToken = token;
        _pairExpiresAt = DateTime.UtcNow.Add(PairWindow);
        return new PhonePairingCode(LocalAddress, _port, token);
    }

    public bool Forget(string deviceId)
    {
        var removed = Devices.Remove(deviceId);
        lock (_connections)
        {
            foreach (var connection in _connections.Where(x => x.DeviceId == deviceId).ToArray())
            {
                Diagnostics.Log($"PhoneLinkService: устройство {deviceId} забыто, соединение закрыто");
                connection.Close();
            }
        }
        if (removed) ConnectionsChanged?.Invoke();
        return removed;
    }

    public void SendToAll(string type, object? payload = null)
    {
        var text = PhoneProtocol.Build(type, payload);
        lock (_connections)
        {
            foreach (var connection in _connections.Where(x => x.IsAuthenticated).ToArray())
                _ = connection.SendTextAsync(text);
        }
    }

    public void SendTo(string deviceId, string type, object? payload = null)
    {
        var text = PhoneProtocol.Build(type, payload);
        lock (_connections)
        {
            foreach (var connection in _connections.Where(x => x.DeviceId == deviceId).ToArray())
                _ = connection.SendTextAsync(text);
        }
    }

    /// <summary>Отправка чанка файла: описание и бинарный кадр, связанные одним id.</summary>
    public void SendChunk(string deviceId, string transferId, long offset, byte[] data)
    {
        var descriptor = PhoneProtocol.Build("file.chunk", new
        {
            id = transferId,
            offset,
            length = data.Length,
            binary = true
        });
        lock (_connections)
        {
            foreach (var connection in _connections.Where(x => x.DeviceId == deviceId).ToArray())
                _ = connection.SendTextAndBinaryAsync(descriptor, data);
        }
    }

    private void OnClientConnected(PhoneSocketConnection connection)
    {
        connection.TextReceived += Connection_TextReceived;
        connection.BinaryReceived += Connection_BinaryReceived;
        lock (_connections) _connections.Add(connection);
        _idleSinceUtc = DateTime.UtcNow;
        Diagnostics.Log($"PhoneLinkService: соединение открыто — {connection.Remote}, ждём auth");
        ConnectionsChanged?.Invoke();
    }

    private void OnClientDisconnected(PhoneSocketConnection connection)
    {
        string? deviceId;
        lock (_connections)
        {
            _connections.Remove(connection);
            deviceId = connection.DeviceId;
            if (_connections.Count == 0) _idleSinceUtc = DateTime.UtcNow;
        }
        if (deviceId is not null)
        {
            Diagnostics.Log($"PhoneLinkService: устройство {deviceId} отключилось");
            DeviceDisconnected?.Invoke(deviceId);
        }
        ConnectionsChanged?.Invoke();
    }

    private void Connection_TextReceived(PhoneSocketConnection connection, string text)
    {
        var envelope = PhoneProtocol.Parse(text);
        if (envelope is null) return;

        try
        {
            if (!connection.IsAuthenticated)
            {
                HandleAuth(connection, envelope);
                return;
            }

            switch (envelope.Type)
            {
                case "ping":
                    _ = connection.SendTextAsync(PhoneProtocol.Build("pong", new { ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));
                    return;
                case "pong":
                    return;
                case "identity":
                    HandleIdentity(connection, envelope);
                    return;
            }

            MessageReceived?.Invoke(connection.DeviceId!, envelope);
        }
        catch (Exception ex) { Diagnostics.Log("PhoneLinkService: обработка сообщения не удалась — " + ex.Message); }
    }

    private void Connection_BinaryReceived(PhoneSocketConnection connection, byte[] payload)
    {
        if (!connection.IsAuthenticated) return;
        try { BinaryReceived?.Invoke(payload); }
        catch (Exception ex) { Diagnostics.Log("PhoneLinkService: бинарный кадр отклонён — " + ex.Message); }
    }

    /// <summary>
    /// Первое сообщение соединения. Токен из QR одноразовый, после успешной
    /// проверки он превращается в постоянный ключ устройства в devices.bin.
    /// </summary>
    private void HandleAuth(PhoneSocketConnection connection, PhoneEnvelope envelope)
    {
        var token = PhoneProtocol.Clamp(envelope.GetString("token"), 128);
        if (string.IsNullOrEmpty(token))
        {
            RejectAsync(connection, "Требуется ключ сопряжения");
            return;
        }

        var name = PhoneProtocol.Clamp(envelope.GetString("name"), 64);
        var known = Devices.Find(token);
        var pairing = _pairToken is not null && _pairExpiresAt > DateTime.UtcNow &&
                      CryptographicOperations.FixedTimeEquals(
                          Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_pairToken));

        PhoneDevice device;
        if (known is not null)
        {
            device = known;
            if (!string.IsNullOrWhiteSpace(name) && name != device.Name)
            {
                device.Name = name;
                Devices.Add(name, token);
            }
        }
        else if (pairing)
        {
            device = Devices.Add(name, token);
            _pairToken = null;
            Diagnostics.Log($"PhoneLinkService: новое сопряжение «{device.Name}» ({device.Id})");
        }
        else
        {
            RejectAsync(connection, "Ключ не принят");
            return;
        }

        connection.DeviceId = device.Id;
        Devices.MarkSeen(device.Id);
        Diagnostics.Log($"PhoneLinkService: устройство «{device.Name}» авторизовано");

        _ = connection.SendTextAsync(PhoneProtocol.Build("auth.ok", new
        {
            deviceId = device.Id,
            name = device.Name,
            server = "Dynamic Island",
            version = "1.3.0"
        }));

        DeviceConnected?.Invoke(device.Id);
        ConnectionsChanged?.Invoke();
    }

    private void HandleIdentity(PhoneSocketConnection connection, PhoneEnvelope envelope)
    {
        var name = PhoneProtocol.Clamp(envelope.GetString("name"), 64);
        if (string.IsNullOrWhiteSpace(name) || connection.DeviceId is null) return;
        var device = Devices.FindById(connection.DeviceId);
        if (device is null || device.Name == name) return;
        device.Name = name;
        var token = device.Token;
        Devices.Add(name, token);
        ConnectionsChanged?.Invoke();
    }

    private static void RejectAsync(PhoneSocketConnection connection, string reason)
    {
        Diagnostics.Log($"PhoneLinkService: соединение {connection.Remote} отклонено — {reason}");
        _ = connection.SendTextAsync(PhoneProtocol.Build("auth.rejected", new { reason }))
            .ContinueWith(_ => connection.Close());
    }

    private async void KeepAliveAsync()
    {
        var now = DateTime.UtcNow;
        PhoneSocketConnection[] snapshot;
        lock (_connections) snapshot = _connections.ToArray();
        foreach (var connection in snapshot)
        {
            try
            {
                if (now - connection.LastPongUtc > DeadAfter)
                {
                    Diagnostics.Log($"PhoneLinkService: {connection.Remote} не отвечает, соединение закрыто");
                    connection.Close();
                    continue;
                }
                await connection.SendPingAsync();
            }
            catch { }
        }
    }

    /// <summary>
    /// Безопасность: если телефон не подключался дольше <see cref="IdleTimeout"/>,
    /// порт перестаёт слушаться. Открытый порт — это открытая дверь в машину,
    /// даже если ключи проверяются; закрываем, когда он явно не нужен.
    /// </summary>
    private void CheckIdle()
    {
        if (!IdleShutdownEnabled) return;
        if (!_server.IsRunning) return;

        bool busy;
        lock (_connections) busy = _connections.Count > 0;
        // Пока действует выданный QR, порт нужен: телефон может сканировать именно сейчас.
        if (_pairToken is not null && _pairExpiresAt > DateTime.UtcNow) busy = true;
        if (busy)
        {
            _idleSinceUtc = DateTime.UtcNow;
            return;
        }

        if (DateTime.UtcNow - _idleSinceUtc < IdleTimeout) return;

        var minutes = (int)IdleTimeout.TotalMinutes;
        Diagnostics.Log($"PhoneLinkService: телефон не подключался {minutes} мин, порт закрыт");
        Stop();
        Status = $"Порт закрыт: телефон не подключался {minutes} мин";
        IdleClosed?.Invoke();
        ConnectionsChanged?.Invoke();
    }

    /// <summary>
    /// Адрес в локальной сети, который увидит телефон. Виртуальные адаптеры
    /// (VirtualBox, Hyper-V, VPN) идут последними: телефон до них не достучится.
    /// </summary>
    public static string FindLocalAddress()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(x => x.GetIPProperties().UnicastAddresses.Select(a => (nic: x, address: a.Address)))
                .Where(x => x.address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x.address))
                .OrderBy(x => IsVirtual(x.nic) ? 1 : 0)
                .ThenBy(x => x.nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
                .ToArray();
            if (candidates.Length > 0) return candidates[0].address.ToString();
        }
        catch (Exception ex) { Diagnostics.Log("PhoneLinkService: поиск адреса не удался — " + ex.Message); }
        return "127.0.0.1";
    }

    private static bool IsVirtual(NetworkInterface nic)
    {
        var name = nic.Name + " " + nic.Description;
        string[] markers = ["virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "loopback", "tap", "tun", "vpn", "bluetooth"];
        return markers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _server.Dispose();
        _gate.Dispose();
    }
}
