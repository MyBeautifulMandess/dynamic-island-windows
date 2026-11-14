using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>Данные сопряжения, которые попадают в QR-код.</summary>
public sealed record PhonePairingCode(string Address, int Port, string Token, string BluetoothAddress = "")
{
    /// <summary>Прямая ссылка на приложение — работает, когда камера открывает свои схемы.</summary>
    public string Url => $"dilink://v1?ip={Address}&port={Port}&token={Token}{BluetoothQuery}";

    /// <summary>
    /// Адрес страницы сопряжения. Именно он кладётся в QR: обычная камера
    /// открывает http-ссылку в браузере, а страница запускает приложение.
    /// </summary>
    public string WebUrl =>
        $"http://{Address}:{Port}{PhonePairingPage.Path}?ip={Address}&port={Port}&token={Token}{BluetoothQuery}";

    public bool HasBluetooth => PhoneBluetoothServer.NormalizeAddress(BluetoothAddress).Length > 0;

    private string BluetoothQuery => HasBluetooth ? $"&bta={Uri.EscapeDataString(BluetoothAddress)}" : "";
}

/// <summary>
/// Общий транспорт для всех телефонных функций. Держит два канала до телефона —
/// WebSocket по локальной сети и RFCOMM по Bluetooth, — но фичевые сервисы
/// видят только поток сообщений: подписаны на <see cref="MessageReceived"/>.
///
/// Каналы живут одновременно и не заменяют друг друга: обрыв Wi-Fi не рвёт
/// связь, пока жив Bluetooth. Отправка идёт по лучшему живому каналу, поэтому
/// телефон не получает одно сообщение дважды.
/// </summary>
public sealed class PhoneLinkService : IDisposable
{
    private static readonly TimeSpan PairWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>Дольше этого молчащий канал считается сомнительным: он уходит в конец очереди отправки.</summary>
    private static readonly TimeSpan FreshAfter = TimeSpan.FromSeconds(45);

    private readonly PhoneSocketServer _server = new();
    private readonly PhoneBluetoothServer _bluetooth = new();
    private readonly List<IPhoneChannel> _channels = [];
    private CancellationTokenSource? _cts;
    private Timer? _keepAlive;
    private Timer? _idleTimer;
    private DateTime _idleSinceUtc = DateTime.UtcNow;
    private string? _pairToken;
    private DateTime _pairExpiresAt;
    private string? _btPairToken;
    private DateTime _btPairExpiresAt;
    private int _port;
    private bool _bluetoothWanted = true;
    private bool _bluetoothStarting;
    private bool _disposed;

    /// <summary>Как долго держать порт открытым, когда телефон не подключён.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Выключатель автозакрытия порта. По умолчанию выключен: пока связь с
    /// телефоном включена, порт слушается постоянно. Закрытый порт — главная
    /// причина «то подключается, то нет»: телефон приходил на мёртвый адрес.
    /// </summary>
    public bool IdleShutdownEnabled { get; set; }

    /// <summary>Порт закрыт по простою: телефон не подключён слишком долго.</summary>
    public event Action? IdleClosed;

    public PhoneDeviceStore Devices { get; } = new();

    /// <summary>
    /// Спросить у пользователя, разрешить ли сопряжение телефона, который сам
    /// нашёл компьютер по Bluetooth. Окно отвечает функцией; если её нет —
    /// разрешаем, иначе телефон не сможет подключиться вовсе.
    /// </summary>
    public Func<string, string, bool>? PairingApprover { get; set; }

    public event Action<string>? DeviceConnected;
    public event Action<string>? DeviceDisconnected;
    public event Action<string, PhoneEnvelope>? MessageReceived;
    public event Action<byte[]>? BinaryReceived;
    public event Action? ConnectionsChanged;

    public PhoneLinkService(int port = 19532)
    {
        _port = port;
        _server.Connected += Register;
        _bluetooth.Connected += Register;
    }

    public bool IsRunning => _server.IsRunning;

    public bool IsBluetoothRunning => _bluetooth.IsRunning;

    public int Port => _port;

    /// <summary>MAC-адрес Bluetooth-адаптера компьютера. Пусто — адаптера нет.</summary>
    public string BluetoothAddress => _bluetooth.LocalAddress;

    /// <summary>Идентификаторы подключённых и авторизованных телефонов.</summary>
    public IReadOnlyList<string> ConnectedDevices
    {
        get
        {
            lock (_channels) return _channels.Where(x => x.IsAuthenticated).Select(x => x.DeviceId!).Distinct().ToArray();
        }
    }

    public bool HasConnections => ConnectedDevices.Count > 0;

    /// <summary>Все каналы устройства через «+»: «Wi-Fi», «Bluetooth», «Wi-Fi + Bluetooth».</summary>
    public string TransportFor(string deviceId)
    {
        var transports = Channels(deviceId).Select(x => x.Transport).Distinct().ToArray();
        return string.Join(" + ", transports);
    }

    public string ActiveTransport
    {
        get
        {
            lock (_channels)
            {
                var transports = _channels.Where(x => x.IsAuthenticated).Select(x => x.Transport).Distinct().ToArray();
                return transports.Length == 0 ? "" : string.Join(" + ", transports);
            }
        }
    }

    public string LocalAddress => FindLocalAddress();

    public string Status { get; private set; } = "Выключено";

    public void Start(int port)
    {
        if (_disposed) return;
        // Перезапускаем только порт: Bluetooth-служба и её соединения живут
        // отдельно и не должны пропадать из-за смены порта или выдачи QR.
        StopPort();
        _port = port;
        try
        {
            _server.Start(port);
            StartTransportTimers();
            Status = $"Ожидание телефона · {LocalAddress}:{port}";
            Diagnostics.Log($"PhoneLinkService: запущен на порту {port}, адрес {LocalAddress}");
        }
        catch (SocketException ex)
        {
            Status = $"Порт {port} занят";
            Diagnostics.Log($"PhoneLinkService: порт {port} недоступен — {ex.Message}");
        }
        catch (Exception ex)
        {
            Status = "Не удалось запустить сервер";
            Diagnostics.Log("PhoneLinkService: запуск не удался — " + ex.Message);
        }

        if (_bluetoothWanted) StartBluetooth();
        ConnectionsChanged?.Invoke();
    }

    /// <summary>Закрывает только сетевой порт и его соединения.</summary>
    private void StopPort()
    {
        try { _keepAlive?.Dispose(); } catch { }
        _keepAlive = null;
        try { _idleTimer?.Dispose(); } catch { }
        _idleTimer = null;
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _server.Stop();
        lock (_channels)
        {
            foreach (var channel in _channels.Where(x => x.Transport != "Bluetooth").ToArray())
                channel.Close();
        }
    }

    /// <summary>
    /// Bluetooth не блокирует запуск: WinRT отвечает через несколько сотен
    /// миллисекунд, а островок к этому моменту уже должен быть готов.
    /// </summary>
    private void StartBluetooth()
    {
        // Настройки и запуск сервера могут позвать почти одновременно. Служба
        // сама ждёт повторный вызов, но лишняя задача и лишние строки в журнале
        // ни к чему: второй запуск просто не нужен.
        if (_bluetoothStarting) return;
        _bluetoothStarting = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var started = await _bluetooth.StartAsync();
                // Пока служба поднималась, её могли выключить: тогда гасим сразу,
                // иначе порт остался бы опубликованным без транспорта в списке.
                if (!_bluetoothWanted || _disposed)
                {
                    _bluetooth.Stop();
                    Diagnostics.Log("Bluetooth: запуск отменён, служба остановлена");
                    return;
                }
                if (started) Diagnostics.Log($"PhoneLinkService: Bluetooth-канал готов, {_bluetooth.LocalAddress}");
            }
            finally { _bluetoothStarting = false; }
            ConnectionsChanged?.Invoke();
        });
    }

    private void StartTransportTimers()
    {
        _cts = new CancellationTokenSource();
        _keepAlive = new Timer(_ => KeepAlive(), null, PingInterval, PingInterval);
        _idleSinceUtc = DateTime.UtcNow;
        _idleTimer = new Timer(_ => CheckIdle(), null, IdleCheckInterval, IdleCheckInterval);
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
        StopBluetooth();
        lock (_channels)
        {
            foreach (var channel in _channels.ToArray()) channel.Close();
            _channels.Clear();
        }
        _pairToken = null;
        _btPairToken = null;
        Status = "Выключено";
        ConnectionsChanged?.Invoke();
    }

    /// <summary>
    /// Гасит только Bluetooth-службу. Нужен отдельно от <see cref="Stop"/>:
    /// закрытие порта по простою не должно рвать Bluetooth-канал, иначе телефон
    /// без общей сети теряет связь на ровном месте.
    /// </summary>
    private void StopBluetooth()
    {
        _bluetooth.Stop();
        lock (_channels)
        {
            foreach (var channel in _channels.Where(x => x.Transport == "Bluetooth").ToArray())
                channel.Close();
        }
    }

    /// <summary>Настройки: включать ли Bluetooth-канал и разрешать ли простой порта.</summary>
    public void Configure(bool bluetooth, bool idleShutdown, TimeSpan idleTimeout)
    {
        _bluetoothWanted = bluetooth;
        IdleShutdownEnabled = idleShutdown;
        IdleTimeout = idleTimeout;
        if (_disposed) return;
        if (bluetooth)
        {
            if (!_bluetooth.IsRunning) StartBluetooth();
        }
        else if (_bluetooth.IsRunning)
        {
            StopBluetooth();
        }
    }

    /// <summary>
    /// Выдаёт одноразовый код сопряжения для QR. Код живёт 10 минут и меняется
    /// при каждом показе, чтобы по старому снимку экрана нельзя было подключиться.
    /// Код — только пропуск на первый вход; дальше у устройства свой ключ.
    /// </summary>
    public PhonePairingCode CreatePairingCode()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _pairToken = token;
        _pairExpiresAt = DateTime.UtcNow.Add(PairWindow);
        return new PhonePairingCode(LocalAddress, _port, token, _bluetooth.LocalAddress);
    }

    public bool Forget(string deviceId)
    {
        var removed = Devices.Remove(deviceId);
        lock (_channels)
        {
            foreach (var channel in _channels.Where(x => x.DeviceId == deviceId).ToArray())
            {
                Diagnostics.Log($"PhoneLinkService: устройство {deviceId} забыто, соединение закрыто");
                channel.Close();
            }
        }
        if (removed) ConnectionsChanged?.Invoke();
        return removed;
    }

    public void SendToAll(string type, object? payload = null)
    {
        var text = PhoneProtocol.Build(type, payload);
        // По одному каналу на устройство: иначе телефон с двумя каналами
        // получил бы сообщение дважды.
        foreach (var channel in Channels().GroupBy(x => x.DeviceId!).Select(group => group.First()))
            _ = channel.SendTextAsync(text);
    }

    public void SendTo(string deviceId, string type, object? payload = null)
    {
        var channel = Channels(deviceId).FirstOrDefault();
        if (channel is null) return;
        _ = channel.SendTextAsync(PhoneProtocol.Build(type, payload));
    }

    /// <summary>
    /// Отправка чанка файла: описание и бинарный кадр, связанные одним id.
    /// Каждый чанк идёт по лучшему живому каналу, поэтому передача переживает
    /// обрыв Wi-Fi: остаток уходит по Bluetooth.
    /// </summary>
    public async Task SendChunkAsync(string deviceId, string transferId, long offset, byte[] data)
    {
        var channel = Channels(deviceId).FirstOrDefault();
        if (channel is null) return;
        var descriptor = PhoneProtocol.Build("file.chunk", new
        {
            id = transferId,
            offset,
            length = data.Length,
            binary = true
        });
        await channel.SendTextAndBinaryAsync(descriptor, data);
    }

    /// <summary>
    /// Авторизованные каналы устройства, лучший первым. Сессий может быть две:
    /// Wi-Fi и Bluetooth работают параллельно, и обрыв одного не рвёт связь.
    /// </summary>
    private IPhoneChannel[] Channels(string? deviceId = null)
    {
        lock (_channels)
        {
            return _channels
                .Where(x => x.IsAuthenticated && (deviceId is null || x.DeviceId == deviceId))
                .OrderBy(ChannelRank)
                .ToArray();
        }
    }

    /// <summary>
    /// Wi-Fi быстрее, поэтому он первый, а давно молчащий канал уходит в конец:
    /// отправка не должна пропадать в сокет, который уже не отвечает.
    /// </summary>
    private static int ChannelRank(IPhoneChannel channel) =>
        (DateTime.UtcNow - channel.LastPongUtc > FreshAfter ? 2 : 0) + (channel.Transport == "Bluetooth" ? 1 : 0);

    private void Register(IPhoneChannel channel)
    {
        channel.Closed += Unregister;
        channel.TextReceived += Channel_TextReceived;
        channel.BinaryReceived += Channel_BinaryReceived;
        lock (_channels) _channels.Add(channel);
        _idleSinceUtc = DateTime.UtcNow;
        Diagnostics.Log($"PhoneLinkService: соединение открыто — {channel.Transport}, {channel.Remote}, ждём auth");
        ConnectionsChanged?.Invoke();
    }

    private void Unregister(IPhoneChannel channel)
    {
        string? deviceId;
        var stillConnected = false;
        lock (_channels)
        {
            if (!_channels.Remove(channel)) return;
            deviceId = channel.DeviceId;
            if (deviceId is not null)
                stillConnected = _channels.Any(x => x.IsAuthenticated && x.DeviceId == deviceId);
            if (_channels.Count == 0) _idleSinceUtc = DateTime.UtcNow;
        }
        channel.Closed -= Unregister;
        channel.TextReceived -= Channel_TextReceived;
        channel.BinaryReceived -= Channel_BinaryReceived;
        if (deviceId is not null)
        {
            Diagnostics.Log($"PhoneLinkService: канал {channel.Transport} устройства {deviceId} закрыт");
            // Второй канал мог остаться: устройство отключилось только тогда,
            // когда не осталось ни одного, иначе панель мигала бы при обрыве Wi-Fi.
            if (!stillConnected) DeviceDisconnected?.Invoke(deviceId);
        }
        ConnectionsChanged?.Invoke();
    }

    private void Channel_TextReceived(IPhoneChannel channel, string text)
    {
        // Любой разборчивый трафик — признак живого канала: телефон присылает
        // уведомления и буфер чаще, чем отвечает на ping, и раньше такой канал
        // всё равно считался мёртвым через 45 секунд молчания.
        if (!string.IsNullOrEmpty(text)) channel.LastPongUtc = DateTime.UtcNow;
        var envelope = PhoneProtocol.Parse(text);
        if (envelope is null) return;

        try
        {
            if (!channel.IsAuthenticated)
            {
                if (envelope.Type == "pairing.hello")
                {
                    HandlePairingHello(channel, envelope);
                    return;
                }
                HandleAuth(channel, envelope);
                return;
            }

            switch (envelope.Type)
            {
                case "ping":
                    channel.LastPongUtc = DateTime.UtcNow;
                    _ = channel.SendTextAsync(PhoneProtocol.Build("pong", new { ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));
                    return;
                case "pong":
                    channel.LastPongUtc = DateTime.UtcNow;
                    return;
                case "identity":
                    HandleIdentity(channel, envelope);
                    return;
            }

            MessageReceived?.Invoke(channel.DeviceId!, envelope);
        }
        catch (Exception ex) { Diagnostics.Log("PhoneLinkService: обработка сообщения не удалась — " + ex.Message); }
    }

    private void Channel_BinaryReceived(IPhoneChannel channel, byte[] payload)
    {
        if (!channel.IsAuthenticated) return;
        channel.LastPongUtc = DateTime.UtcNow;
        try { BinaryReceived?.Invoke(payload); }
        catch (Exception ex) { Diagnostics.Log("PhoneLinkService: бинарный кадр отклонён — " + ex.Message); }
    }

    /// <summary>
    /// Первое знакомство без камеры и без общей сети: телефон сам находит
    /// компьютер по Bluetooth и просит ключ. Так же устроены Quick Share и
    /// связь с телефоном у Windows — устройство нашли рядом, человек
    /// подтвердил, и пошёл обмен.
    /// </summary>
    private async void HandlePairingHello(IPhoneChannel channel, PhoneEnvelope envelope)
    {
        try
        {
            var name = PhoneProtocol.Clamp(envelope.GetString("name"), 64);
            var approved = PairingApprover?.Invoke(name, channel.Transport) ?? true;
            if (!approved)
            {
                Diagnostics.Log($"PhoneLinkService: сопряжение по Bluetooth отклонено — {name}");
                await channel.SendTextAsync(PhoneProtocol.Build("pairing.rejected",
                    new { reason = "Сопряжение отклонено на компьютере" }));
                channel.Close();
                return;
            }

            // Код одноразовый и живёт десять минут: телефон входит по нему уже
            // своим каналом, а не тем, по которому пришёл запрос.
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _btPairToken = token;
            _btPairExpiresAt = DateTime.UtcNow.Add(PairWindow);
            // Без общей сети локальный адрес — петля, и телефону он бесполезен:
            // оставляем пустым, чтобы связь шла только по Bluetooth.
            var address = LocalAddress;
            var offer = new
            {
                token,
                ip = address == "127.0.0.1" ? "" : address,
                port = _port,
                name = Environment.MachineName,
                bta = _bluetooth.LocalAddress,
                expires = new DateTimeOffset(_btPairExpiresAt.ToUniversalTime()).ToUnixTimeSeconds()
            };
            Diagnostics.Log($"PhoneLinkService: сопряжение — «{name}», ключ выдан");
            await channel.SendTextAsync(PhoneProtocol.Build("pairing.offer", offer));
        }
        catch (Exception ex)
        {
            Diagnostics.Log("PhoneLinkService: ответ на запрос сопряжения не удался — " + ex.Message);
        }
    }

    /// <summary>
    /// Первое сообщение соединения. Телефон присылает свой постоянный ключ,
    /// если уже сопрягался, или одноразовый код из QR — при первой встрече.
    /// Ключ из QR принимается только один раз: после успешного входа телефон
    /// получает в <c>auth.ok</c> постоянный ключ и дальше ходит с ним.
    /// </summary>
    private void HandleAuth(IPhoneChannel channel, PhoneEnvelope envelope)
    {
        var token = PhoneProtocol.Clamp(envelope.GetString("token"), 128);
        if (string.IsNullOrEmpty(token))
        {
            Reject(channel, "Требуется ключ сопряжения");
            return;
        }

        var name = PhoneProtocol.Clamp(envelope.GetString("name"), 64);
        var instance = PhoneProtocol.Clamp(envelope.GetString("instance"), 64);
        // По Bluetooth MAC известен транспорту — это переживает переустановку
        // приложения; телефон может прислать Android ID в самом сообщении.
        var hardware = PhoneProtocol.Clamp(envelope.GetString("hwid"), 64);
        if (string.IsNullOrEmpty(hardware)) hardware = PhoneProtocol.Clamp(channel.HardwareAddress, 64);
        var known = Devices.FindByToken(token);
        var pairing = _pairToken is not null && _pairExpiresAt > DateTime.UtcNow && SecretEquals(token, _pairToken);
        var btPairing = _btPairToken is not null && _btPairExpiresAt > DateTime.UtcNow && SecretEquals(token, _btPairToken);

        PhoneDevice device;
        if (known is not null)
        {
            // Ключ найден, но установка могла переехать (переустановка, смена
            // идентификатора): дописываем признаки к той же записи, а не заводим вторую.
            device = Devices.Adopt(name, instance, hardware, token, out _);
        }
        else if (pairing || btPairing)
        {
            var created = false;
            device = Devices.Adopt(name, instance, hardware, token, out created);
            // Код одноразовый: второй раз он уже не сработает.
            if (pairing) _pairToken = null;
            if (btPairing) _btPairToken = null;
            Diagnostics.Log(created
                ? $"PhoneLinkService: новое сопряжение «{device.Name}» ({device.Id}, {channel.Transport})"
                : $"PhoneLinkService: повторный вход «{device.Name}» ({device.Id}, {channel.Transport})");
        }
        else
        {
            Reject(channel, "Ключ не принят");
            return;
        }

        channel.DeviceId = device.Id;
        channel.LastPongUtc = DateTime.UtcNow;
        Devices.MarkSeen(device.Id, channel.Transport);
        // Второй канал того же устройства — это не новое подключение: устройство
        // уже было на связи, и повторный снимок планера ему не нужен.
        var firstChannel = !Channels(device.Id).Any(other => other != channel);
        Diagnostics.Log($"PhoneLinkService: устройство «{device.Name}» авторизовано по {channel.Transport}" +
                        (firstChannel ? "" : $" (каналов: {Channels(device.Id).Length})"));

        _ = channel.SendTextAsync(PhoneProtocol.Build("auth.ok", new
        {
            deviceId = device.Id,
            name = device.Name,
            token = device.Token,
            transport = channel.Transport,
            server = "Dynamic Island",
            version = AppVersion
        }));

        if (firstChannel) DeviceConnected?.Invoke(device.Id);
        ConnectionsChanged?.Invoke();
    }

    /// <summary>Версия сборки: телефон показывает её в строке состояния.</summary>
    private static string AppVersion
    {
        get
        {
            var version = typeof(PhoneLinkService).Assembly.GetName().Version;
            return version is null ? "1.8.1" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    private void HandleIdentity(IPhoneChannel channel, PhoneEnvelope envelope)
    {
        var name = PhoneProtocol.Clamp(envelope.GetString("name"), 64);
        var instance = PhoneProtocol.Clamp(envelope.GetString("instance"), 64);
        if (string.IsNullOrWhiteSpace(name) || channel.DeviceId is null) return;
        var device = Devices.FindById(channel.DeviceId);
        if (device is null) return;
        var hardware = PhoneProtocol.Clamp(envelope.GetString("hwid"), 64);
        if (string.IsNullOrEmpty(hardware)) hardware = channel.HardwareAddress;
        if (device.Name == name && (string.IsNullOrEmpty(instance) || device.Instance == instance) &&
            (string.IsNullOrEmpty(hardware) || device.Hardware == hardware)) return;
        Devices.Adopt(name, string.IsNullOrEmpty(instance) ? device.Instance : instance, hardware, device.Token, out _);
        ConnectionsChanged?.Invoke();
    }

    private static bool SecretEquals(string left, string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static void Reject(IPhoneChannel channel, string reason)
    {
        Diagnostics.Log($"PhoneLinkService: соединение {channel.Remote} отклонено — {reason}");
        _ = channel.SendTextAsync(PhoneProtocol.Build("auth.rejected", new { reason }))
            .ContinueWith(_ => channel.Close());
    }

    private void KeepAlive()
    {
        var now = DateTime.UtcNow;
        IPhoneChannel[] snapshot;
        lock (_channels) snapshot = _channels.ToArray();
        foreach (var channel in snapshot)
        {
            try
            {
                if (now - channel.LastPongUtc > DeadAfter)
                {
                    Diagnostics.Log($"PhoneLinkService: {channel.Transport} {channel.Remote} не отвечает, соединение закрыто");
                    channel.Close();
                    continue;
                }
                _ = channel.SendPingAsync();
            }
            catch { }
        }
    }

    /// <summary>
    /// Безопасность: если телефон не подключался дольше <see cref="IdleTimeout"/>,
    /// сетевой порт перестаёт слушаться. Открытый порт — это открытая дверь в
    /// машину, даже если ключи проверяются; закрываем, когда он явно не нужен.
    /// Bluetooth при этом продолжает работать: он не слушает сеть, а живёт
    /// поверх сопряжённой пары.
    /// </summary>
    private void CheckIdle()
    {
        if (!IdleShutdownEnabled) return;
        if (!_server.IsRunning) return;

        bool busy;
        lock (_channels) busy = _channels.Count > 0;
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
        StopPort();
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
        _bluetooth.Dispose();
    }
}
