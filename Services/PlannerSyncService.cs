using System.IO;
using System.Text.Json;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>
/// Связывает календарь и напоминания островка с телефоном.
///
/// Локальные записи, созданные без подключения, никуда не деваются: они живут в
/// обычных файлах приложения, а сервис держит рядом «зеркало» с отметками времени
/// изменения. При подключении обе стороны присылают полный снимок, и слияние идёт
/// по правилу «новее — побеждает». Удаления хранятся надгробиями, иначе стёртая на
/// одной стороне запись возвращалась бы со второй. Устройство, которое ещё ни разу
/// не подключалось, ничего не портит: его снимок просто применяется при первом
/// соединении.
/// </summary>
public sealed class PlannerSyncService : IDisposable
{
    private readonly PhoneLinkService _link;
    private readonly string _path = Path.Combine(AppPaths.DataDirectory, "planner-mirror.json");
    private readonly Dictionary<string, PlannerRecord> _mirror = new();
    private readonly object _gate = new();
    private readonly HashSet<string> _handshaked = [];
    private readonly Func<PhoneLinkSettings> _settings;
    private bool _disposed;

    /// <summary>Записи, которые нужно применить к коллекциям окна.</summary>
    public event Action<PlannerRecord>? Applied;

    public event Action? Merged;

    /// <summary>Число записей, ожидающих отправки: показывается в панели «Телефон».</summary>
    public int PendingCount { get; private set; }

    public string Status { get; private set; } = "";

    public PlannerSyncService(PhoneLinkService link, Func<PhoneLinkSettings> settings)
    {
        _link = link;
        _settings = settings;
        Load();
        link.MessageReceived += OnMessage;
        link.DeviceConnected += OnDeviceConnected;
        link.DeviceDisconnected += id =>
        {
            lock (_handshaked) _handshaked.Remove(id);
            UpdatePending();
        };
        link.ConnectionsChanged += UpdatePending;
    }

    /// <summary>Полный снимок этой стороны — для отправки телефону.</summary>
    public object Snapshot() => new { records = Records() };

    private PlannerRecord[] Records()
    {
        lock (_gate) return _mirror.Values.ToArray();
    }

    public PlannerRecord? Find(string id)
    {
        lock (_gate) return _mirror.GetValueOrDefault(id);
    }

    /// <summary>
    /// Переносит в зеркало записи, жившие в приложении до появления связи с
    /// телефоном. Уже известные записи не трогаются: их версия новее и она
    /// победит при слиянии. Отправка не выполняется — снимок уйдёт при подключении.
    /// </summary>
    public void Seed(IEnumerable<CalendarEventItem> events, IEnumerable<ReminderItem> reminders)
    {
        var seed = new List<PlannerRecord>();
        foreach (var item in events)
        {
            seed.Add(new PlannerRecord
            {
                Id = item.Id,
                Kind = PlannerRecord.EventKind,
                Title = item.Title,
                Start = new DateTimeOffset(DateTime.SpecifyKind(item.Start, DateTimeKind.Local)).ToUnixTimeMilliseconds(),
                End = new DateTimeOffset(DateTime.SpecifyKind(item.End, DateTimeKind.Local)).ToUnixTimeMilliseconds(),
                AllDay = item.IsAllDay,
                ReminderMinutes = item.ReminderMinutes,
                ReminderId = item.ReminderId
            });
        }
        foreach (var item in reminders)
        {
            seed.Add(new PlannerRecord
            {
                Id = item.Id,
                Kind = PlannerRecord.ReminderKind,
                Title = item.Title,
                Due = new DateTimeOffset(DateTime.SpecifyKind(item.Due, DateTimeKind.Local)).ToUnixTimeMilliseconds(),
                Done = item.Done,
                CompletedAt = item.CompletedAt is { } completed
                    ? new DateTimeOffset(DateTime.SpecifyKind(completed, DateTimeKind.Local)).ToUnixTimeMilliseconds()
                    : null,
                Source = item.Source,
                EventId = item.EventId
            });
        }

        var added = false;
        lock (_gate)
        {
            foreach (var record in seed)
            {
                if (_mirror.ContainsKey(record.Id)) continue;
                // Время правки — самое старое: телефонные данные с более свежим
                // штампом при слиянии остаются в приоритете.
                record.Updated = 1;
                _mirror[record.Id] = record;
                added = true;
            }
        }
        if (added) Save();
    }

    /// <summary>
    /// Локальная правка. Запись попадает в зеркало с новым временем изменения и
    /// сразу уходит телефону, если он подключён; иначе остаётся ждать соединения.
    /// </summary>
    public void Upsert(PlannerRecord record)
    {
        if (string.IsNullOrEmpty(record.Id)) record.Id = Guid.NewGuid().ToString("N");
        record.Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        record.Deleted = false;
        lock (_gate)
        {
            _mirror[record.Id] = record;
        }
        Save();
        Broadcast(record);
    }

    /// <summary>Удаление локальной записи: остаётся надгробие, чтобы оно дошло до телефона.</summary>
    public void Tombstone(string id, string kind)
    {
        PlannerRecord record;
        lock (_gate)
        {
            if (!_mirror.TryGetValue(id, out var existing))
            {
                existing = new PlannerRecord { Id = id, Kind = kind };
            }
            record = new PlannerRecord { Id = id, Kind = existing.Kind, Title = existing.Title, Deleted = true };
        }
        record.Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_gate) _mirror[id] = record;
        Save();
        Broadcast(record);
    }

    /// <summary>Принимает и одиночные правки, и полный снимок от телефона.</summary>
    private void OnMessage(string deviceId, PhoneEnvelope envelope)
    {
        if (envelope.Type is not ("planner.push" or "planner.sync")) return;
        if (!_settings().PlannerSync) return;
        var nodes = new List<System.Text.Json.Nodes.JsonNode?>();
        if (envelope.Payload["records"] is System.Text.Json.Nodes.JsonArray array) nodes.AddRange(array);
        if (envelope.Payload["record"] is System.Text.Json.Nodes.JsonObject single) nodes.Add(single);
        if (nodes.Count == 0) return;

        var changed = new List<PlannerRecord>();
        lock (_gate)
        {
            foreach (var node in nodes)
            {
                PlannerRecord? incoming;
                try { incoming = node?.Deserialize<PlannerRecord>(PhoneProtocol.Json); }
                catch { continue; }
                if (incoming is null || string.IsNullOrEmpty(incoming.Id)) continue;
                if (_mirror.TryGetValue(incoming.Id, out var known) && known.Updated >= incoming.Updated) continue;
                incoming.Origin = "phone";
                _mirror[incoming.Id] = incoming;
                changed.Add(incoming);
            }
        }
        if (changed.Count == 0) return;
        Save();
        foreach (var record in changed) Applied?.Invoke(record);
        Merged?.Invoke();
        UpdatePending();
        // Эхо намеренно не отправляется: свой снимок мы отдали при подключении,
        // а ответный снимок телефона — это уже его данные. Ответ на каждый снимок
        // превратился бы в бесконечный обмен.
    }

    private void OnDeviceConnected(string deviceId)
    {
        lock (_handshaked) _handshaked.Add(deviceId);
        if (!_settings().PlannerSync) return;
        // Сразу отдаём полный снимок: всё, что накопилось без телефона, уходит разом.
        _link.SendTo(deviceId, "planner.sync", Snapshot());
        UpdatePending();
    }

    /// <summary>Слияние пришло от телефона в ответ на наш снимок.</summary>
    public void MergeFrom(PhoneEnvelope envelope, string deviceId)
    {
        OnMessage(deviceId, envelope);
    }

    private void Broadcast(PlannerRecord record)
    {
        if (!_settings().PlannerSync) return;
        if (!_link.HasConnections) return;
        _link.SendToAll("planner.push", new { records = new[] { record } });
        UpdatePending();
    }

    public void UpdatePending()
    {
        lock (_gate)
        {
            var online = _link.HasConnections;
            PendingCount = online ? 0 : _mirror.Values.Count(x => !x.Deleted);
        }
        Status = _link.HasConnections
            ? $"{_mirror.Count(x => !x.Value.Deleted)} записей синхронизировано"
            : $"{PendingCount} записей ждут подключения";
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var raw = SecretProtector.Unprotect(File.ReadAllText(_path));
            if (string.IsNullOrWhiteSpace(raw)) return;
            var list = JsonSerializer.Deserialize<List<PlannerRecord>>(raw, PhoneProtocol.Json) ?? [];
            lock (_gate)
            {
                foreach (var record in list) _mirror[record.Id] = record;
            }
        }
        catch (Exception ex) { Diagnostics.Log("PlannerSyncService: зеркало не загружено — " + ex.Message); }
    }

    private void Save()
    {
        try
        {
            PlannerRecord[] snapshot;
            lock (_gate) snapshot = _mirror.Values.ToArray();
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, SecretProtector.Protect(JsonSerializer.Serialize(snapshot, PhoneProtocol.Json)));
            File.Move(temp, _path, true);
        }
        catch (Exception ex) { Diagnostics.Log("PlannerSyncService: зеркало не сохранено — " + ex.Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _link.MessageReceived -= OnMessage;
    }
}
