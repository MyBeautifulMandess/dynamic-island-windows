using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>
/// Связующий слой «телефон ↔ функции островка». Держит транспорт, сопряжение,
/// синхронизацию буфера и планера, а также передаёт окну события для панелей.
/// Само окно не работает с WebSocket напрямую.
/// </summary>
public sealed class PhoneLinkCoordinator : IDisposable
{
    private static readonly TimeSpan ClipboardDebounce = TimeSpan.FromMilliseconds(500);

    private readonly PhoneLinkService _link;
    private readonly ClipboardService _clipboard;
    private readonly Timer _clipboardTimer;
    private string _pendingClipboard = "";
    private bool _disposed;

    public PhoneLinkService Link => _link;
    public PhoneFileService Files { get; }
    public PhoneNotificationService Notifications { get; }
    public PhoneMediaService Media { get; }
    public PlannerSyncService Planner { get; }
    public PhoneDeviceStore Devices => _link.Devices;

    /// <summary>Текст, скопированный на телефоне. Окно добавляет его в историю и показывает карточку.</summary>
    public event Action<string>? ClipboardFromPhone;

    /// <summary>Состояние подключения для панелей и пилюли.</summary>
    public event Action? StateChanged;

    public Func<PhoneLinkSettings> SettingsProvider { get; set; } = () => new();

    public PhoneLinkCoordinator(ClipboardService clipboard, string downloadsFolder)
    {
        _clipboard = clipboard;
        _link = new PhoneLinkService();
        Files = new PhoneFileService(_link, () => SettingsProvider(), () => downloadsFolder);
        Notifications = new PhoneNotificationService();
        Media = new PhoneMediaService(_link);
        Planner = new PlannerSyncService(_link, () => SettingsProvider());
        _clipboardTimer = new Timer(_ => FlushClipboard(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Attach()
    {
        var settings = SettingsProvider();
        Notifications.Attach(_link, () => SettingsProvider());
        Media.Attach(() => SettingsProvider());
        Files.Attach();
        _clipboard.RemoteChanged += OnLocalClipboard;
        _link.MessageReceived += OnMessage;
        _link.ConnectionsChanged += () => StateChanged?.Invoke();
        _link.IdleClosed += () => StateChanged?.Invoke();
        _link.IdleShutdownEnabled = settings.IdleShutdown;
        _link.IdleTimeout = TimeSpan.FromMinutes(settings.IdleMinutes);
        if (settings.Enabled) Start(settings.Port);
    }

    public void Start(int port) => _link.Start(port);

    public void Stop() => _link.Stop();

    public bool IsRunning => _link.IsRunning;

    public string Status => _link.Status;

    public int PendingPlanner => Planner.PendingCount;

    /// <summary>Ключ сопряжения и адрес для QR-кода.</summary>
    public PhonePairingCode CreatePairingCode() => _link.CreatePairingCode();

    public void Forget(string deviceId) => _link.Forget(deviceId);

    private void OnLocalClipboard(ClipboardItem item)
    {
        if (!SettingsProvider().ClipboardSync) return;
        if (!_link.HasConnections) return;
        if (item.Origin == "phone") return;
        _pendingClipboard = item.Text;
        // Дебаунс: пока пользователь копирует быстро, на телефон уходит только финал.
        _clipboardTimer.Change(ClipboardDebounce, Timeout.InfiniteTimeSpan);
    }

    private void FlushClipboard()
    {
        var text = _pendingClipboard;
        _pendingClipboard = "";
        if (string.IsNullOrEmpty(text)) return;
        if (!SettingsProvider().ClipboardSync || !_link.HasConnections) return;
        _link.SendToAll("clipboard.push", new { text, origin = "pc" });
    }

    private void OnMessage(string deviceId, PhoneEnvelope envelope)
    {
        if (envelope.Type != "clipboard.push") return;
        if (!SettingsProvider().ClipboardSync) return;
        var text = PhoneProtocol.Clamp(envelope.GetString("text"), 20000);
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text == _clipboard.LastRemoteText) return;
        // Сначала история островка (метка «Телефон»), затем системный буфер:
        // по ТЗ текст должен оказаться и там, и там.
        _clipboard.AddRemote(text, "phone");
        _clipboard.ApplyRemote(text);
        ClipboardFromPhone?.Invoke(text);
    }

    /// <summary>Настройки поменялись: включаем или выключаем сервер и доносим переключатели до сервисов.</summary>
    public void ApplySettings()
    {
        var settings = SettingsProvider();
        _link.IdleShutdownEnabled = settings.IdleShutdown;
        _link.IdleTimeout = TimeSpan.FromMinutes(settings.IdleMinutes);
        if (settings.Enabled && !_link.IsRunning) Start(settings.Port);
        else if (!settings.Enabled && _link.IsRunning) Stop();
        else if (settings.Enabled && _link.IsRunning && _link.Port != settings.Port) Start(settings.Port);
        Files.CleanupStaleParts();
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clipboardTimer.Dispose();
        _clipboard.RemoteChanged -= OnLocalClipboard;
        Notifications.Dispose();
        Media.Dispose();
        Planner.Dispose();
        Files.Dispose();
        _link.Dispose();
    }
}
