using System.IO;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Radios;
using Windows.Networking;
using Windows.Networking.Sockets;

namespace DynamicIsland.Services;

/// <summary>
/// Bluetooth-сервер: публикует RFCOMM-службу с известным UUID и принимает
/// соединения. Телефон и компьютер к этому моменту уже сопряжены средствами
/// Windows — так же устроена связь с телефоном у Google (Quick Share) и
/// Microsoft (Phone Link): системное сопряжение, поверх него свой канал.
///
/// Основной канал шифруется: <see cref="SocketProtectionLevel.BluetoothEncryptionWithAuthentication"/>
/// требует сопряжённую пару и включает шифрование ссылки, которого нет у
/// обычного Wi-Fi транспорта. Для первого знакомства, когда системной пары
/// ещё нет, рядом висит открытая служба <see cref="PairingServiceUuid"/>:
/// по ней телефон получает ключ сопряжения и уходит в основной канал.
/// </summary>
internal sealed class PhoneBluetoothServer : IDisposable
{
    /// <summary>UUID службы DI Link. Тот же записан в приложении телефона.</summary>
    public static readonly Guid ServiceUuid = new("6e400001-b5a3-f393-e0a9-e50e24dcca9e");

    /// <summary>
    /// Служба первого знакомства. Основной канал зашифрован и требует системной
    /// пары, а телефон ещё не сопряжён с компьютером — значит, первый разговор
    /// идёт по открытой службе. Ключ сопряжения идёт в этом разговоре один раз.
    /// </summary>
    public static readonly Guid PairingServiceUuid = new("6e400002-b5a3-f393-e0a9-e50e24dcca9e");

    private RfcommServiceProvider? _provider;
    private StreamSocketListener? _listener;
    private RfcommServiceProvider? _pairProvider;
    private StreamSocketListener? _pairListener;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>
    /// Пока идёт запуск, второй вызов ждёт его результата. Без этого настройки
    /// поднимали службу одновременно с запуском сервера, и в журнале появлялось
    /// две одинаковые службы — вторая затирала первую и её сокет утекал.
    /// </summary>
    private Task<bool>? _starting;

    public event Action<PhoneBluetoothConnection>? Connected;
    public event Action<PhoneBluetoothConnection>? Disconnected;

    public bool IsRunning => _listener is not null;

    /// <summary>MAC-адрес адаптера компьютера: его получает телефон при сопряжении.</summary>
    public string LocalAddress { get; private set; } = "";

    public string ServiceName => _provider?.ServiceId.AsString() ?? "";

    /// <summary>
    /// Поднимает службу. Возвращает false, если адаптера нет, он выключен или
    /// порт занят: Wi-Fi транспорт при этом продолжает работать.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (_disposed) return false;
        if (_listener is not null) return true;
        // Запуск уже идёт: ждём его, а не начинаем второй параллельно.
        if (_starting is { } running) return await running;
        var start = StartCoreAsync();
        _starting = start;
        try { return await start; }
        finally { if (ReferenceEquals(_starting, start)) _starting = null; }
    }

    private async Task<bool> StartCoreAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null)
            {
                Diagnostics.Log("Bluetooth: адаптер не найден");
                return false;
            }
            if (!adapter.IsClassicSupported)
            {
                Diagnostics.Log("Bluetooth: адаптер не поддерживает классический профиль");
                return false;
            }

            var radio = await adapter.GetRadioAsync();
            if (radio.State != RadioState.On)
            {
                Diagnostics.Log($"Bluetooth: радио {radio.State}, служба не поднята");
                return false;
            }

            LocalAddress = FormatAddress(adapter.BluetoothAddress);

            var provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(ServiceUuid));
            var listener = new StreamSocketListener();
            listener.ConnectionReceived += OnConnectionReceived;

            // Шифрование требует сопряжённую пару. Если адаптер его не даёт,
            // служба всё равно поднимается: авторизация ключом остаётся.
            var level = SocketProtectionLevel.BluetoothEncryptionWithAuthentication;
            var encrypted = true;
            try
            {
                await listener.BindServiceNameAsync(provider.ServiceId.AsString(), level);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Bluetooth: шифрование недоступно, обычный канал — " + ex.Message);
                level = SocketProtectionLevel.PlainSocket;
                encrypted = false;
                await listener.BindServiceNameAsync(provider.ServiceId.AsString(), level);
            }

            provider.StartAdvertising(listener, true);
            _provider = provider;
            _listener = listener;
            _cts = new CancellationTokenSource();
            Diagnostics.Log($"Bluetooth: служба {ServiceUuid} поднята, адрес {LocalAddress}, защита {level}");

            // Пока основной канал требует системной пары, рядом висит открытая
            // служба: по ней телефон здоровается и получает ключ сопряжения.
            if (encrypted) await StartPairingService();
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Bluetooth: запуск не удался — " + ex.Message);
            Stop();
            return false;
        }
    }

    /// <summary>Открытая служба для первого знакомства: телефон ещё не сопряжён с компьютером.</summary>
    private async Task StartPairingService()
    {
        try
        {
            var provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(PairingServiceUuid));
            var listener = new StreamSocketListener();
            listener.ConnectionReceived += OnConnectionReceived;
            await listener.BindServiceNameAsync(provider.ServiceId.AsString(), SocketProtectionLevel.PlainSocket);
            provider.StartAdvertising(listener, true);
            _pairProvider = provider;
            _pairListener = listener;
            Diagnostics.Log($"Bluetooth: служба сопряжения {PairingServiceUuid} поднята");
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Bluetooth: служба сопряжения не поднялась — " + ex.Message);
            try { _pairProvider?.StopAdvertising(); } catch { }
            _pairProvider = null;
            try { _pairListener?.Dispose(); } catch { }
            _pairListener = null;
        }
    }

    private async void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        PhoneBluetoothConnection? connection = null;
        try
        {
            var remote = await ResolveRemoteAsync(args.Socket);
            var address = PhoneBluetoothServer.NormalizeAddress(remote);
            connection = new PhoneBluetoothConnection(args.Socket, remote, address);
            Diagnostics.Log($"Bluetooth: соединение — {remote}");
            Connected?.Invoke(connection);
            var token = _cts?.Token ?? CancellationToken.None;
            _ = WatchAsync(connection, token);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Bluetooth: соединение не принято — " + ex.Message);
            connection?.Dispose();
            try { args.Socket.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// MAC телефона. У Bluetooth-сокета в RawName лежит адрес адаптера, но
    /// формат зависит от драйвера, поэтому основной путь — система: она знает
    /// пару и вернёт <see cref="BluetoothDevice"/> с готовым адресом.
    /// Возвращается уже нормализованный адрес: по нему сливаются записи.
    /// </summary>
    private static async Task<string> ResolveRemoteAsync(StreamSocket socket)
    {
        var host = socket.Information.RemoteAddress;
        if (host is null) return "?";
        try
        {
            var device = await BluetoothDevice.FromHostNameAsync(host);
            if (device is not null)
            {
                var address = FormatAddress(device.BluetoothAddress);
                if (!string.IsNullOrEmpty(address)) return address;
            }
        }
        catch (Exception ex) { Diagnostics.Log("Bluetooth: адрес удалённого устройства не получен — " + ex.Message); }
        return NormalizeAddress(host.RawName) is { Length: > 0 } normalized ? normalized : host.RawName;
    }

    /// <summary>
    /// Чтение вынесено в отдельную задачу: обработчик WinRT не должен держать
    /// поток, пока телефон присылает и принимает данные.
    /// </summary>
    private async Task WatchAsync(PhoneBluetoothConnection connection, CancellationToken token)
    {
        try { await connection.ReadLoopAsync(token); }
        catch (Exception ex) { Diagnostics.Log("Bluetooth: канал закрылся — " + ex.Message); }
        finally
        {
            Disconnected?.Invoke(connection);
            connection.Dispose();
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        try { _provider?.StopAdvertising(); } catch { }
        _provider = null;
        try { _listener?.Dispose(); } catch { }
        _listener = null;
        try { _pairProvider?.StopAdvertising(); } catch { }
        _pairProvider = null;
        try { _pairListener?.Dispose(); } catch { }
        _pairListener = null;
        Diagnostics.Log("Bluetooth: служба остановлена");
    }

    /// <summary>MAC в виде DC:45:46:A3:82:97 — этот формат понимает Android.</summary>
    public static string FormatAddress(ulong address)
    {
        var hex = address.ToString("X12");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    /// <summary>Приводит адрес из QR или от системы к тому же виду.</summary>
    public static string NormalizeAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var digits = new string(raw.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return digits.Length == 12 ? FormatAddress(Convert.ToUInt64(digits, 16)) : "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
