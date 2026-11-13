using System.Text;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>
/// Уведомления Android-телефона. Приходят пачками, поэтому несколько событий
/// подряд склеиваются в одну карточку, а неубираемые и отключённые приложения
/// отфильтровываются до попадания в общий список.
/// </summary>
public sealed class PhoneNotificationService : INotificationSink, IDisposable
{
    public const string IdPrefix = "phone:";

    private const int Limit = 50;
    private const int MaxBody = 300;
    private static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(300);

    private readonly Dictionary<string, NotificationItem> _items = new();
    private readonly Dictionary<string, StringBuilder> _pending = new();
    private readonly Timer _flushTimer;
    private readonly object _gate = new();
    private PhoneLinkService? _link;
    private Func<PhoneLinkSettings> _settings = () => new();
    private bool _disposed;

    public event Action<IReadOnlyList<NotificationItem>>? Changed;
    public event Action<NotificationItem>? Received;

    public PhoneNotificationService()
    {
        _flushTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Status { get; private set; } = "";

    public IReadOnlyList<NotificationItem> Items =>
        _items.Values.OrderByDescending(x => x.Created).Take(Limit).ToArray();

    public void Attach(PhoneLinkService link, Func<PhoneLinkSettings> settings)
    {
        _link = link;
        _settings = settings;
        link.MessageReceived += OnMessage;
        link.DeviceConnected += _ => Status = "Телефон подключён";
        link.DeviceDisconnected += _ => Status = "";
    }

    private void OnMessage(string deviceId, PhoneEnvelope envelope)
    {
        if (envelope.Type != "notification.push") return;
        var settings = _settings();
        if (!settings.Notifications) return;

        var app = PhoneProtocol.Clamp(envelope.GetString("app"), 40);
        var package = PhoneProtocol.Clamp(envelope.GetString("package"), 80);
        var key = PhoneProtocol.Clamp(envelope.GetString("key"), 160);
        var title = PhoneProtocol.Clamp(envelope.GetString("title"), 120);
        var body = PhoneProtocol.Clamp(envelope.GetString("body"), MaxBody);
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) return;
        if (IsIgnored(settings, app, package)) return;

        var device = _link?.Devices.FindById(deviceId);
        var deviceName = device?.Name ?? "Телефон";
        var id = IdPrefix + (string.IsNullOrWhiteSpace(key) ? Guid.NewGuid().ToString("N") : key);
        if (string.IsNullOrEmpty(title)) title = app;

        var item = new NotificationItem(id, string.IsNullOrWhiteSpace(app) ? "Телефон" : app, title, body,
            DateTime.Now)
        {
            Source = "phone",
            DeviceName = deviceName,
            RemoteKey = key
        };

        lock (_gate)
        {
            if (!_pending.TryGetValue(id, out var buffer))
            {
                _pending[id] = buffer = new StringBuilder();
                _flushTimer.Change(MergeWindow, Timeout.InfiniteTimeSpan);
            }
            else if (buffer.Length > 0)
            {
                buffer.Append("; ");
            }
            if (!string.IsNullOrWhiteSpace(body)) buffer.Append(body);
            if (_items.ContainsKey(id))
            {
                _items[id] = item;
                return;
            }
            _items[id] = item;
            while (_items.Count > Limit)
            {
                var oldest = _items.Values.OrderBy(x => x.Created).First();
                _items.Remove(oldest.Id);
            }
        }

        Changed?.Invoke(Items);
        if (!IsQuiet(item)) Received?.Invoke(item);
    }

    private bool _inFlush;

    private void Flush()
    {
        if (_inFlush) return;
        _inFlush = true;
        try
        {
            List<NotificationItem> updated;
            lock (_gate)
            {
                if (_pending.Count == 0) return;
                updated = [];
                foreach (var (id, buffer) in _pending)
                {
                    if (!_items.TryGetValue(id, out var item) || buffer.Length == 0) continue;
                    var merged = item with { Body = PhoneProtocol.Clamp(buffer.ToString(), MaxBody) };
                    _items[id] = merged;
                    updated.Add(merged);
                }
                _pending.Clear();
            }
            if (updated.Count > 0) Changed?.Invoke(Items);
        }
        catch (Exception ex) { Diagnostics.Log("PhoneNotificationService: склейка не удалась — " + ex.Message); }
        finally { _inFlush = false; }
    }

    private static bool IsIgnored(PhoneLinkSettings settings, string app, string package)
    {
        var list = settings.IgnoredAppList;
        if (list.Length == 0) return false;
        return list.Any(entry =>
            (!string.IsNullOrEmpty(package) && entry.Equals(package, StringComparison.OrdinalIgnoreCase)) ||
            entry.Equals(app, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Своё собственное приложение не должно показывать карточку о самом себе.</summary>
    private static bool IsQuiet(NotificationItem item) =>
        item.AppName.Contains("DI Link", StringComparison.OrdinalIgnoreCase) ||
        item.AppName.Contains("Dynamic Island", StringComparison.OrdinalIgnoreCase);

    /// <summary>Убирает карточку и просит Android снять уведомление из шторки.</summary>
    public void Dismiss(string id)
    {
        NotificationItem? item;
        lock (_gate)
        {
            if (!_items.Remove(id, out item)) return;
            _pending.Remove(id);
        }
        if (item is not null && !string.IsNullOrEmpty(item.RemoteKey))
            _link?.SendToAll("notify.dismissed", new { key = item.RemoteKey });
        Changed?.Invoke(Items);
    }

    public void ClearAll()
    {
        List<NotificationItem> removed;
        lock (_gate)
        {
            removed = _items.Values.ToList();
            _items.Clear();
            _pending.Clear();
        }
        foreach (var item in removed.Where(x => !string.IsNullOrEmpty(x.RemoteKey)))
            _link?.SendToAll("notify.dismissed", new { key = item.RemoteKey });
        if (removed.Count > 0) Changed?.Invoke([]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Dispose();
        if (_link is not null) _link.MessageReceived -= OnMessage;
    }
}
