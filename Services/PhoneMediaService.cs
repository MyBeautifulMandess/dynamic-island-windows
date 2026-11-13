using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>
/// Медиа с телефона. Телефон присылает состояние только по событиям (пауза,
/// смена трека, перемотка), а позицию между событиями островок достраивает
/// локальным таймером — так в минуту воспроизведения в канал не уходит ничего.
/// </summary>
public sealed class PhoneMediaService : IDisposable
{
    private readonly PhoneLinkService _link;
    private Func<PhoneLinkSettings> _settings = () => new();
    private MediaSnapshot? _snapshot;
    private DateTime _updatedAt = DateTime.MinValue;
    private bool _paused;
    private bool _disposed;

    public event Action<MediaSnapshot>? Changed;
    public event Action<MediaSnapshot>? TrackChanged;

    public PhoneMediaService(PhoneLinkService link) => _link = link;

    public void Attach(Func<PhoneLinkSettings> settings)
    {
        _settings = settings;
        _link.MessageReceived += OnMessage;
        _link.DeviceDisconnected += _ => Clear();
    }

    public bool HasActiveMedia => _snapshot is { HasMedia: true };

    public string SourceApp { get; private set; } = "";

    /// <summary>
    /// Снимок с учётом прошедшего времени. Когда ничего не играет дольше пяти
    /// минут, источник снимается и островок возвращается к локальному плееру.
    /// </summary>
    public MediaSnapshot? Current
    {
        get
        {
            if (_snapshot is not { HasMedia: true }) return null;
            if (_paused && DateTime.UtcNow - _updatedAt > TimeSpan.FromMinutes(5)) return null;
            if (!_snapshot.IsPlaying) return _snapshot;
            var elapsed = (DateTime.UtcNow - _updatedAt).TotalSeconds;
            var position = Math.Clamp(_snapshot.Position + Math.Max(0, elapsed), 0, Math.Max(0, _snapshot.Duration));
            return _snapshot with { Position = position };
        }
    }

    private void OnMessage(string deviceId, PhoneEnvelope envelope)
    {
        if (envelope.Type != "media.state") return;
        if (!_settings().MediaSync)
        {
            if (HasActiveMedia) Clear();
            return;
        }

        if (!envelope.GetBool("hasMedia"))
        {
            Clear();
            return;
        }

        var title = PhoneProtocol.Clamp(envelope.GetString("title"), 200);
        var artist = PhoneProtocol.Clamp(envelope.GetString("artist"), 120);
        var app = PhoneProtocol.Clamp(envelope.GetString("app"), 60);
        var duration = Math.Max(0, envelope.Get<double>("duration"));
        var position = Math.Max(0, envelope.Get<double>("position"));
        var isPlaying = envelope.GetBool("isPlaying");
        var artwork = DecodeArtwork(envelope.GetString("artwork"));

        var previous = _snapshot;
        var track = $"{title}\0{artist}";
        var changed = previous is null || $"{previous.Title}\0{previous.Artist}" != track;
        var artworkSource = artwork ?? (changed ? null : previous?.Artwork);

        SourceApp = string.IsNullOrWhiteSpace(app) ? "Телефон" : app;
        var snapshot = new MediaSnapshot(
            string.IsNullOrWhiteSpace(title) ? "Без названия" : title,
            string.IsNullOrWhiteSpace(artist) ? SourceApp : artist,
            $"Телефон · {SourceApp}",
            true,
            isPlaying,
            artworkSource,
            position,
            duration > 0 ? duration : 1);

        _snapshot = snapshot;
        _updatedAt = DateTime.UtcNow;
        _paused = !isPlaying;

        Changed?.Invoke(snapshot);
        if (changed && previous is not null) TrackChanged?.Invoke(snapshot);
    }

    private static ImageSource? DecodeArtwork(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64) || base64.Length > 120_000) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var memory = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 240;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    public void Toggle() => _link.SendToAll("media.command", new { action = "toggle" });
    public void Next() => _link.SendToAll("media.command", new { action = "next" });
    public void Previous() => _link.SendToAll("media.command", new { action = "prev" });
    public void Seek(double seconds) => _link.SendToAll("media.command", new { action = "seek", position = (int)Math.Max(0, seconds) });

    /// <summary>Телефон отключился или прислал hasMedia: false — возвращаем локальный источник.</summary>
    public void Clear()
    {
        if (_snapshot is null) return;
        _snapshot = null;
        _paused = false;
        SourceApp = "";
        Changed?.Invoke(new MediaSnapshot("Ничего не воспроизводится", "Медиа появится здесь", "", false, false, null, 0, 0));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _link.MessageReceived -= OnMessage;
    }
}
