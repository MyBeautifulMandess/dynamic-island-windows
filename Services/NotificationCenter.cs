using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>
/// Общий «сток» уведомлений: и Windows, и телефон публикуют карточки через него,
/// поэтому временный островок не знает, откуда пришло уведомление.
/// </summary>
public interface INotificationSink
{
    event Action<IReadOnlyList<NotificationItem>>? Changed;
    event Action<NotificationItem>? Received;
    IReadOnlyList<NotificationItem> Items { get; }
    string Status { get; }
    void Dismiss(string id);
    void ClearAll();
}

/// <summary>
/// Объединённый список уведомлений Windows и телефона. Карточки сортируются по
/// времени, поэтому телефонные сообщения встают в общую историю, а не отдельно.
/// </summary>
public sealed class NotificationCenter : INotificationSink, IDisposable
{
    private const int Limit = 50;
    private readonly NotificationService _windows;
    private readonly PhoneNotificationService _phone;
    private readonly object _gate = new();
    private IReadOnlyList<NotificationItem> _items = [];

    public NotificationCenter(NotificationService windows, PhoneNotificationService phone)
    {
        _windows = windows;
        _phone = phone;
        _windows.Changed += _ => Rebuild();
        _phone.Changed += _ => Rebuild();
        _windows.Received += item => Received?.Invoke(item);
        _phone.Received += item => Received?.Invoke(item);
    }

    public event Action<IReadOnlyList<NotificationItem>>? Changed;
    public event Action<NotificationItem>? Received;

    public IReadOnlyList<NotificationItem> Items
    {
        get { lock (_gate) return _items; }
    }

    public string Status => _windows.Status;

    private void Rebuild()
    {
        var merged = _windows.Items.Concat(_phone.Items)
            .OrderByDescending(x => x.Created)
            .Take(Limit)
            .ToArray();
        lock (_gate)
        {
            if (Same(_items, merged)) return;
            _items = merged;
        }
        Changed?.Invoke(Items);
    }

    private static bool Same(IReadOnlyList<NotificationItem> left, IReadOnlyList<NotificationItem> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (!Equals(left[i], right[i])) return false;
        return true;
    }

    /// <summary>
    /// Маршрутизация удаления: у телефонных карточек id начинается с "phone:",
    /// остальные уходят в Центр уведомлений Windows.
    /// </summary>
    public void Dismiss(string id)
    {
        if (id.StartsWith(PhoneNotificationService.IdPrefix, StringComparison.Ordinal))
        {
            _phone.Dismiss(id);
            return;
        }
        _windows.Dismiss(id);
    }

    public void ClearAll()
    {
        _windows.ClearAll();
        _phone.ClearAll();
        Rebuild();
    }

    public async Task InitializeAsync()
    {
        await _windows.InitializeAsync();
        Rebuild();
    }

    public Task RefreshAsync() => _windows.RefreshAsync(false);

    public void Dispose()
    {
        _windows.Dispose();
        _phone.Dispose();
    }
}
