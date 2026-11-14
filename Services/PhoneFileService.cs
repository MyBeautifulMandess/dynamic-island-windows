using System.IO;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>Ход одной передачи файла для карточки прогресса на островке.</summary>
public sealed record FileTransferProgress(string Id, string Name, string Direction, long Transferred, long Total)
{
    public int Percent => Total <= 0 ? 0 : (int)Math.Clamp(Transferred * 100 / Total, 0, 100);
    public bool Incoming => Direction == "phone-to-pc";
    public string Title => Incoming ? $"Получение · {Name}" : $"Отправка · {Name}";
    public string Details => $"{FormatSize(Transferred)} из {FormatSize(Total)}  ·  {Percent}%";

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / 1073741824d:0.0} ГБ",
        >= 1024 * 1024 => $"{bytes / 1048576d:0.0} МБ",
        >= 1024 => $"{bytes / 1024d:0.0} КБ",
        _ => $"{bytes} Б"
    };
}

/// <summary>
/// Приём и отправка файлов по каналу телефона. Входящие пишутся во временный
/// «.part» рядом с целью и переименовываются только после подтверждения, поэтому
/// оборванная передача не оставляет битых файлов в папке загрузок.
/// </summary>
public sealed class PhoneFileService : IDisposable
{
    private const int ChunkSize = 64 * 1024;
    private const long AckEvery = 1024 * 1024;
    private const long MaxBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan OfferTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StalePartAge = TimeSpan.FromHours(24);

    private readonly PhoneLinkService _link;
    private readonly Func<PhoneLinkSettings> _settings;
    private readonly Func<string> _fallbackFolder;
    private readonly Dictionary<string, Incoming> _incoming = new();
    private readonly Dictionary<string, Outgoing> _outgoing = new();
    private readonly object _gate = new();

    /// <summary>Последний чанк, пришедший бинарным кадром, вместе с его описанием.</summary>
    private PendingChunk? _pendingChunk;

    public event Action<FileTransferProgress>? Progress;
    public event Action<FileTransferProgress>? Completed;
    public event Action<string>? Failed;
    public event Action<string>? FileReceived;

    public PhoneFileService(PhoneLinkService link, Func<PhoneLinkSettings> settings, Func<string> fallbackFolder)
    {
        _link = link;
        _settings = settings;
        _fallbackFolder = fallbackFolder;
        link.MessageReceived += OnMessage;
        link.BinaryReceived += OnBinary;
    }

    public string Status { get; private set; } = "";

    public void Attach() => CleanupStaleParts();

    private void OnMessage(string deviceId, PhoneEnvelope envelope)
    {
        var settings = _settings();
        switch (envelope.Type)
        {
            case "file.offer" when settings.FileTransfer:
                AcceptOffer(deviceId, envelope);
                break;
            case "file.chunk":
                PrepareChunk(envelope);
                break;
            case "file.done":
                FinishIncoming(envelope.GetString("id"));
                break;
            case "file.cancel":
                CancelIncoming(envelope.GetString("id"));
                break;
            case "file.ack":
                Acknowledge(deviceId, envelope);
                break;
        }
    }

    private void AcceptOffer(string deviceId, PhoneEnvelope envelope)
    {
        var id = envelope.GetString("id");
        var name = SafeName(envelope.GetString("name"));
        var size = envelope.GetLong("size");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return;
        if (!string.Equals(envelope.GetString("dir"), "phone-to-pc", StringComparison.OrdinalIgnoreCase)) return;
        if (size <= 0 || size > MaxBytes)
        {
            Fail(id, "Файл слишком большой — максимум 2 ГБ");
            _link.SendTo(deviceId, "file.cancel", new { id, reason = "size" });
            return;
        }

        try
        {
            var folder = ResolveFolder();
            Directory.CreateDirectory(folder);
            var target = UniquePath(folder, name);
            var part = Path.Combine(folder, $".di-{id}.part");
            lock (_gate)
            {
                _incoming[id] = new Incoming(id, target, part, size, 0);
                _pendingChunk = null;
            }
            Diagnostics.Log($"Файлы: приём «{name}» ({size} байт) → {folder}");
            _link.SendTo(deviceId, "file.ack", new { id, offset = 0 });
            Report(id, name, "phone-to-pc", 0, size);
        }
        catch (Exception ex)
        {
            Fail(id, "Не удалось создать файл: " + ex.Message);
            _link.SendTo(deviceId, "file.cancel", new { id, reason = "storage" });
        }
    }

    private void PrepareChunk(PhoneEnvelope envelope)
    {
        var id = envelope.GetString("id");
        if (string.IsNullOrEmpty(id)) return;
        lock (_gate)
            _pendingChunk = new PendingChunk(id, envelope.GetLong("offset"), envelope.GetLong("length"));
    }

    /// <summary>
    /// Бинарный кадр не содержит id, поэтому он сопоставляется с последним
    /// описанием <c>file.chunk</c>. Канал однопоточный: порядок гарантирован.
    /// Данные пишутся по смещению из описания, а не по своему счётчику: если
    /// чанк потерялся или пришёл повторно, счётчик уехал бы и файл испортился.
    /// </summary>
    private void OnBinary(byte[] payload)
    {
        Incoming? incoming = null;
        PendingChunk? pending = null;
        lock (_gate)
        {
            pending = _pendingChunk;
            _pendingChunk = null;
            if (pending is { } value) _incoming.TryGetValue(value.Id, out incoming);
        }
        if (payload.Length == 0 || incoming is null || pending is not { } chunk) return;
        try
        {
            var offset = Math.Max(0, chunk.Offset);
            if (offset > incoming.Total) { Fail(incoming.Id, "Смещение чанка вне файла"); return; }
            using (var stream = new FileStream(incoming.Part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                // Файл заранее растягивается до полного размера: Windows тогда
                // выделяет место одним куском, а не по чанку, и проверка размера
                // в конце работает без отдельного счётчика.
                if (stream.Length != incoming.Total) stream.SetLength(incoming.Total);
                stream.Seek(offset, SeekOrigin.Begin);
                var room = (int)Math.Min(payload.Length, incoming.Total - offset);
                if (room > 0) stream.Write(payload, 0, room);
            }
            incoming.Received = Math.Max(incoming.Received, offset + Math.Min(payload.Length, incoming.Total - offset));
            var received = incoming.Received;
            var shouldAck = received % AckEvery < ChunkSize || received >= incoming.Total;
            if (shouldAck)
            {
                var deviceId = FindDevice();
                if (deviceId is not null) _link.SendTo(deviceId, "file.ack", new { id = incoming.Id, offset = received });
                incoming.Reported = received;
            }
            Report(incoming.Id, incoming.Name, "phone-to-pc", received, incoming.Total);
        }
        catch (Exception ex) { Fail(incoming.Id, "Запись файла прервана: " + ex.Message); }
    }

    private void FinishIncoming(string id)
    {
        Incoming? incoming;
        lock (_gate)
        {
            if (!_incoming.Remove(id, out incoming)) return;
        }
        try
        {
            // Файл заранее растянут до полного размера, поэтому длина на диске
            // ничего не говорит: полноту даёт только счётчик принятых байт.
            if (incoming.Received < incoming.Total)
            {
                Diagnostics.Log($"Файлы: «{incoming.Name}» недополучен ({incoming.Received}/{incoming.Total})");
                TryDelete(incoming.Part);
                Fail(id, "Передача прервалась — файл не сохранён");
                return;
            }
            if (File.Exists(incoming.Part))
                File.Move(incoming.Part, incoming.Target, true);
            var info = new FileInfo(incoming.Target);
            Diagnostics.Log($"Файлы: «{incoming.Name}» сохранён ({info.Length} байт)");
            Report(id, incoming.Name, "phone-to-pc", incoming.Total, incoming.Total);
            Completed?.Invoke(new FileTransferProgress(id, incoming.Name, "phone-to-pc", incoming.Total, incoming.Total));
            FileReceived?.Invoke(incoming.Target);
        }
        catch (Exception ex) { Fail(id, "Не удалось сохранить файл: " + ex.Message); }
    }

    private void CancelIncoming(string id)
    {
        Incoming? incoming;
        lock (_gate)
        {
            if (!_incoming.Remove(id, out incoming)) return;
        }
        try { if (File.Exists(incoming.Part)) File.Delete(incoming.Part); } catch { }
        Fail(id, "Передача отменена");
    }
    /// <summary>Отправка файла с компьютера на телефон.</summary>
    public void Send(string deviceId, string path)
    {
        var name = SafeName(Path.GetFileName(path));
        long size;
        try { size = new FileInfo(path).Length; }
        catch (Exception ex) { Failed?.Invoke("Файл не найден: " + ex.Message); return; }
        if (size <= 0) { Failed?.Invoke("Пустой файл не отправляется"); return; }
        if (size > MaxBytes) { Failed?.Invoke("Файл больше 2 ГБ не поддерживается"); return; }
        if (!_settings().FileTransfer) { Failed?.Invoke("Передача файлов выключена в настройках"); return; }

        var id = Guid.NewGuid().ToString("N");
        var outgoing = new Outgoing(id, name, path, size, deviceId);
        lock (_gate) _outgoing[id] = outgoing;
        _link.SendTo(deviceId, "file.offer", new { id, name, size, mime = "application/octet-stream", dir = "pc-to-phone" });
        Report(id, name, "pc-to-phone", 0, size);

        // Телефон мог не ответить вовсе (нет разрешения, занят): без таймаута
        // передача висела бы в списке вечно.
        _ = Task.Run(async () =>
        {
            await Task.Delay(OfferTimeout);
            bool pending;
            lock (_gate) pending = _outgoing.ContainsKey(id) && !_outgoing[id].Started;
            if (!pending) return;
            Fail(id, "Телефон не ответил на предложение файла");
            _link.SendTo(deviceId, "file.cancel", new { id, reason = "timeout" });
        });
    }

    /// <summary>
    /// Отправка начинается только после <c>file.ack</c> с offset = 0: телефон
    /// сам решает, куда писать, и может отказаться.
    /// </summary>
    private void Acknowledge(string deviceId, PhoneEnvelope envelope)
    {
        var id = envelope.GetString("id");
        lock (_gate)
        {
            if (!_outgoing.TryGetValue(id, out var outgoing)) return;
            if (outgoing.Started)
            {
                outgoing.Offset = Math.Max(outgoing.Offset, envelope.GetLong("offset"));
                return;
            }
            if (envelope.GetLong("offset") != 0) return;
            outgoing.Started = true;
        }
        _ = PumpAsync(id);
    }

    private async Task PumpAsync(string id)
    {
        Outgoing? outgoing;
        lock (_gate)
        {
            if (!_outgoing.TryGetValue(id, out outgoing)) return;
        }
        var buffer = new byte[ChunkSize];
        try
        {
            await using var stream = new FileStream(outgoing.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long offset = 0;
            while (offset < outgoing.Total)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, ChunkSize));
                if (read <= 0) break;
                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                // Ждём отправки: без этого чанки копились в памяти быстрее, чем
                // уходили в сокет, и на большом файле канал захлёбывался.
                await _link.SendChunkAsync(outgoing.DeviceId, id, offset, chunk);
                offset += read;
                lock (_gate) outgoing.Offset = offset;
                Report(id, outgoing.Name, "pc-to-phone", offset, outgoing.Total);
            }
            _link.SendTo(outgoing.DeviceId, "file.done", new { id });
            lock (_gate) _outgoing.Remove(id);
        }
        catch (Exception ex)
        {
            Fail(id, "Отправка прервалась: " + ex.Message);
            _link.SendTo(outgoing.DeviceId, "file.cancel", new { id, reason = "read" });
        }
    }

    public void Cancel(string id)
    {
        Outgoing? cancelled = null;
        lock (_gate)
        {
            if (_outgoing.Remove(id, out var outgoing))
            {
                cancelled = outgoing;
            }
            else if (_incoming.Remove(id, out var incoming))
            {
                TryDelete(incoming.Part);
                Failed?.Invoke("Приём отменён");
            }
        }
        if (cancelled is null) return;
        _link.SendTo(cancelled.DeviceId, "file.cancel", new { id, reason = "user" });
        Failed?.Invoke("Отправка отменена");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private string? FindDevice() => _link.ConnectedDevices.FirstOrDefault();

    private string ResolveFolder()
    {
        var configured = _settings().PcDownloadFolder.Trim();
        return string.IsNullOrWhiteSpace(configured) ? _fallbackFolder() : configured;
    }

    /// <summary>
    /// Оставляет только имя файла: путь, диск и «..» отбрасываются, служебные
    /// символы заменяются, зарезервированные имена Windows экранируются.
    /// </summary>
    public static string SafeName(string? raw)
    {
        var name = (raw ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = name.Trim('.', ' ');
        if (name.Length > 120)
        {
            var extension = Path.GetExtension(name);
            if (extension.Length > 10) extension = "";
            name = name[..Math.Max(1, 120 - extension.Length)] + extension;
        }
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") name = "file";
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3"];
        var stem = Path.GetFileNameWithoutExtension(name);
        if (reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) name = "_" + name;
        return name;
    }

    private static string UniquePath(string folder, string name)
    {
        var target = Path.Combine(folder, name);
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 1; i < 1000; i++)
        {
            target = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(target) && !Directory.Exists(target)) return target;
        }
        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private void Report(string id, string name, string direction, long transferred, long total) =>
        Progress?.Invoke(new FileTransferProgress(id, name, direction, transferred, total));

    private void Fail(string id, string message)
    {
        lock (_gate)
        {
            _outgoing.Remove(id);
            if (_incoming.TryGetValue(id, out var incoming))
            {
                _incoming.Remove(id);
                try { if (File.Exists(incoming.Part)) File.Delete(incoming.Part); } catch { }
            }
        }
        Failed?.Invoke(message);
    }

    /// <summary>
    /// Убирает недокачанные «.part» старше суток: после сбоя связи они уже не нужны,
    /// а места занимают столько же, сколько исходный файл.
    /// </summary>
    public void CleanupStaleParts()
    {
        try
        {
            var folder = ResolveFolder();
            if (!Directory.Exists(folder)) return;
            var removed = 0;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles(".di-*.part"))
            {
                try
                {
                    if (DateTime.UtcNow - file.LastWriteTimeUtc < StalePartAge) continue;
                    file.Delete();
                    removed++;
                }
                catch { }
            }
            if (removed > 0) Diagnostics.Log($"Файлы: удалено брошенных .part — {removed}");
        }
        catch (Exception ex) { Diagnostics.Log("Файлы: уборка не удалась — " + ex.Message); }
    }

    public void Dispose()
    {
        _link.MessageReceived -= OnMessage;
        _link.BinaryReceived -= OnBinary;
        lock (_gate)
        {
            foreach (var incoming in _incoming.Values)
            {
                try { if (File.Exists(incoming.Part)) File.Delete(incoming.Part); } catch { }
            }
            _incoming.Clear();
            _outgoing.Clear();
        }
    }

    private sealed class Incoming(string id, string target, string part, long total, long received)
    {
        public string Id { get; } = id;
        public string Name { get; } = Path.GetFileName(target);
        public string Target { get; } = target;
        public string Part { get; } = part;
        public long Total { get; } = total;
        public long Received { get; set; } = received;

        /// <summary>Сколько уже подтверждено телефону — чтобы не слать ack на каждый чанк.</summary>
        public long Reported { get; set; }
    }

    private sealed class Outgoing(string id, string name, string path, long total, string deviceId)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Path { get; } = path;
        public long Total { get; } = total;
        public string DeviceId { get; } = deviceId;
        public bool Started { get; set; }
        public long Offset { get; set; }
    }

    private readonly record struct PendingChunk(string Id, long Offset, long Length);
}
