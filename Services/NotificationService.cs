using DynamicIsland.Models;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Services;

public sealed class NotificationService : INotificationSink, IDisposable
{
    private UserNotificationListener? _listener;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Dictionary<string, NotificationItem> _items = new();
    private CancellationTokenSource? _pollCts;
    private bool _initialized;
    private bool _disposed;
    private string _lastSnapshot = "";

    public event Action<IReadOnlyList<NotificationItem>>? Changed;
    public event Action<NotificationItem>? Received;
    public string Status { get; private set; } = "Запрос доступа…";

    public async Task InitializeAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
        }
        catch (Exception ex)
        {
            Status = "Слушатель уведомлений недоступен: " + ex.Message;
            Diagnostics.Log("NotificationService: listener unavailable — " + ex.Message);
            Changed?.Invoke([]);
            return;
        }

        try
        {
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                Status = "Доступ к уведомлениям не разрешён";
                Changed?.Invoke([]);
                return;
            }

            Status = "Уведомления подключены";
            await RefreshAsync(false);
            try { _listener.NotificationChanged += OnNotificationChanged; }
            catch { }
            StartPollingFallback();
            _initialized = true;
        }
        catch (Exception ex)
        {
            Status = "Недоступно: " + ex.Message;
            Diagnostics.Log("NotificationService: init failed — " + ex.Message);
        }
    }

    private async void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args) =>
        await RefreshAsync(true);

    private void StartPollingFallback()
    {
        if (_pollCts is not null) return;
        _pollCts = new CancellationTokenSource();
        _ = PollAsync(_pollCts.Token);
    }

    private async Task PollAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                await RefreshAsync(true);
        }
        catch (OperationCanceledException) { }
    }

    public async Task RefreshAsync(bool announce)
    {
        if (_listener is null) return;
        if (!await _refreshLock.WaitAsync(0)) return;
        try
        {
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var fresh = new List<NotificationItem>();
            var activeIds = new HashSet<string>();
            foreach (var notification in notifications)
            {
                activeIds.Add(notification.Id.ToString());
                if (_items.TryGetValue(notification.Id.ToString(), out var known))
                {
                    fresh.Add(known);
                    continue;
                }

                var item = Map(notification);
                _items[notification.Id.ToString()] = item;
                fresh.Add(item);
                if (announce && _initialized) Received?.Invoke(item);
            }

            foreach (var id in _items.Keys.Where(id => !activeIds.Contains(id)).ToArray())
                _items.Remove(id);
            var ordered = fresh.OrderByDescending(x => x.Created).Take(50).ToArray();
            var snapshot = string.Join('\u001f', ordered.Select(x => $"{x.Id}:{x.Title}:{x.Body}"));
            if (snapshot != _lastSnapshot)
            {
                _lastSnapshot = snapshot;
                Changed?.Invoke(ordered);
            }
        }
        catch (Exception ex)
        {
            Status = "Ошибка чтения: " + ex.Message;
            Diagnostics.Log("NotificationService: refresh failed — " + ex.Message);
        }
        finally { _refreshLock.Release(); }
    }

    private static NotificationItem Map(UserNotification notification)
    {
        var appName = "Windows";
        try { appName = notification.AppInfo.DisplayInfo.DisplayName; } catch { }
        var lines = new List<string>();
        try
        {
            var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding is not null)
                lines.AddRange(binding.GetTextElements().Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        catch { }
        var title = lines.FirstOrDefault() ?? appName;
        var body = string.Join("\n", lines.Skip(1));
        return new NotificationItem(notification.Id.ToString(), appName, title, body, notification.CreationTime.LocalDateTime);
    }

    public IReadOnlyList<NotificationItem> Items =>
        _items.Values.OrderByDescending(x => x.Created).Take(50).ToArray();

    public void Dismiss(string id)
    {
        if (uint.TryParse(id, out var numeric))
        {
            try { _listener?.RemoveNotification(numeric); } catch { }
        }
        if (_items.Remove(id)) NotifyChanged();
    }

    /// <summary>
    /// Убирает все уведомления из Центра уведомлений Windows и из локального списка.
    /// </summary>
    public void ClearAll()
    {
        foreach (var id in _items.Keys.ToArray())
        {
            if (uint.TryParse(id, out var numeric))
            {
                try { _listener?.RemoveNotification(numeric); } catch { }
            }
        }
        var changed = _items.Count > 0;
        _items.Clear();
        _lastSnapshot = "";
        if (changed)
        {
            Status = "Уведомления очищены";
            Changed?.Invoke([]);
        }
    }

    private void NotifyChanged()
    {
        var ordered = _items.Values.OrderByDescending(x => x.Created).Take(50).ToArray();
        _lastSnapshot = string.Join('\u001f', ordered.Select(x => $"{x.Id}:{x.Title}:{x.Body}"));
        Changed?.Invoke(ordered);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        try { if (_listener is not null) _listener.NotificationChanged -= OnNotificationChanged; } catch { }
        _refreshLock.Dispose();
    }
}
