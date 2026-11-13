namespace DynamicIsland.Models;

/// <summary>
/// Настройки связи с телефоном по локальной сети. Живут внутри <see cref="AppSettings"/>,
/// поэтому сохраняются тем же файлом, что и остальные параметры.
/// </summary>
public sealed class PhoneLinkSettings : ObservableObject
{
    private bool _enabled;
    private int _port = 19532;
    private bool _clipboardSync = true;
    private bool _clipboardPreview;
    private bool _notifications = true;
    private bool _fileTransfer = true;
    private bool _mediaSync = true;
    private bool _plannerSync = true;
    private string _ignoredApps = "";
    private string _pcDownloadFolder = "";

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public int Port { get => _port; set => Set(ref _port, Math.Clamp(value, 1024, 65535)); }
    public bool ClipboardSync { get => _clipboardSync; set => Set(ref _clipboardSync, value); }

    /// <summary>Показывать мини-карточку при тексте с телефона. По умолчанию выключено.</summary>
    public bool ClipboardPreview { get => _clipboardPreview; set => Set(ref _clipboardPreview, value); }

    public bool Notifications { get => _notifications; set => Set(ref _notifications, value); }
    public bool FileTransfer { get => _fileTransfer; set => Set(ref _fileTransfer, value); }
    public bool MediaSync { get => _mediaSync; set => Set(ref _mediaSync, value); }
    public bool PlannerSync { get => _plannerSync; set => Set(ref _plannerSync, value); }

    /// <summary>Имена пакетов Android, уведомления которых не показываются. Через запятую или с новой строки.</summary>
    public string IgnoredApps { get => _ignoredApps; set => Set(ref _ignoredApps, value); }

    /// <summary>Куда складывать файлы с телефона. Пусто — папка «Загрузки».</summary>
    public string PcDownloadFolder { get => _pcDownloadFolder; set => Set(ref _pcDownloadFolder, value); }

    private bool _idleShutdown = true;
    private int _idleMinutes = 15;

    /// <summary>
    /// Закрывать порт, когда телефон не подключён дольше указанного времени.
    /// Порт перестаёт слушаться, а нулевой запрос из сети не получит даже ответа.
    /// </summary>
    public bool IdleShutdown { get => _idleShutdown; set => Set(ref _idleShutdown, value); }

    /// <summary>Сколько минут без телефона держать порт открытым. 1–240.</summary>
    public int IdleMinutes { get => _idleMinutes; set => Set(ref _idleMinutes, Math.Clamp(value, 1, 240)); }

    public string[] IgnoredAppList => IgnoredApps
        .Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
