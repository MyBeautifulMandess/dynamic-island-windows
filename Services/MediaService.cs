using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Models;
using Windows.Media.Control;

namespace DynamicIsland.Services;

public sealed record MediaSnapshot(string Title, string Artist, string Source, bool HasMedia, bool IsPlaying,
    ImageSource? Artwork, double Position, double Duration);

public sealed class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string _lastTrack = "";
    private ImageSource? _artwork;
    private string _artworkTrack = "";
    private bool _disposed;

    public event Action<MediaSnapshot>? Changed;
    public event Action<MediaSnapshot>? TrackChanged;

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
            await AttachCurrentAsync();
        }
        catch { PublishEmpty(); }
    }

    private async Task AttachCurrentAsync()
    {
        if (_manager is null) return;
        var current = _manager.GetCurrentSession();
        if (ReferenceEquals(current, _session))
        {
            await RefreshAsync();
            return;
        }
        DetachSession();
        _session = current;
        _artwork = null;
        _artworkTrack = "";
        if (_session is null)
        {
            PublishEmpty();
            return;
        }
        _session.MediaPropertiesChanged += Session_Changed;
        _session.PlaybackInfoChanged += Session_Changed;
        _session.TimelinePropertiesChanged += Session_Changed;
        await RefreshAsync();
    }

    private async void Manager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args) => await AttachCurrentAsync();

    private async void Session_Changed(GlobalSystemMediaTransportControlsSession sender, object args) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_session is null || !await _refreshLock.WaitAsync(0)) return;
        try
        {
            var properties = await _session.TryGetMediaPropertiesAsync();
            var playback = _session.GetPlaybackInfo();
            var timeline = _session.GetTimelineProperties();
            var title = string.IsNullOrWhiteSpace(properties.Title) ? "Без названия" : properties.Title;
            var artist = properties.Artist;
            if (string.IsNullOrWhiteSpace(artist)) artist = properties.AlbumTitle;
            var trackKey = $"{title}\0{artist}";
            if (_artworkTrack != trackKey)
            {
                _artwork = await LoadArtworkAsync(properties);
                _artworkTrack = trackKey;
            }
            var duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
            var isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var elapsed = isPlaying ? (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds : 0;
            var position = Math.Clamp(timeline.Position.TotalSeconds + Math.Max(0, elapsed), 0, duration > 0 ? duration : double.MaxValue);
            var snapshot = new MediaSnapshot(title, artist, FriendlySource(_session.SourceAppUserModelId), true,
                isPlaying,
                _artwork, position, duration);
            Changed?.Invoke(snapshot);
            if (_lastTrack != trackKey)
            {
                if (!string.IsNullOrEmpty(_lastTrack)) TrackChanged?.Invoke(snapshot);
                _lastTrack = trackKey;
            }
        }
        catch { }
        finally { _refreshLock.Release(); }
    }

    private static async Task<ImageSource?> LoadArtworkAsync(GlobalSystemMediaTransportControlsSessionMediaProperties properties)
    {
        if (properties.Thumbnail is null) return null;
        try
        {
            using var stream = await properties.Thumbnail.OpenReadAsync();
            using var input = stream.AsStreamForRead();
            using var memory = new MemoryStream();
            await input.CopyToAsync(memory);
            memory.Position = 0;
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

    private static string FriendlySource(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Windows Media";
        var clean = id.Split('!')[0].Split('\\')[^1];
        return clean.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
    }

    private void PublishEmpty()
    {
        _lastTrack = "";
        Changed?.Invoke(new MediaSnapshot("Ничего не воспроизводится", "Медиа появится здесь", "", false, false, null, 0, 0));
    }

    public async Task TogglePlayAsync() { if (_session is not null) await _session.TryTogglePlayPauseAsync(); }
    public async Task NextAsync() { if (_session is not null) await _session.TrySkipNextAsync(); }
    public async Task PreviousAsync() { if (_session is not null) await _session.TrySkipPreviousAsync(); }
    public async Task SeekAsync(double seconds)
    {
        if (_session is not null) await _session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(seconds).Ticks);
    }

    private void DetachSession()
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= Session_Changed;
            _session.PlaybackInfoChanged -= Session_Changed;
            _session.TimelinePropertiesChanged -= Session_Changed;
        }
        _session = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_manager is not null) _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
        DetachSession();
        _refreshLock.Dispose();
    }
}
