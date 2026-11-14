using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>
/// Сопряжённый телефон. <see cref="Token"/> — постоянный ключ: он выдаётся
/// при первом сопряжении и живёт, пока устройство не забудут. Одноразовый
/// ключ из QR в записи не попадает, иначе каждое новое сканирование кода
/// заводило бы ещё одну копию того же телефона.
/// </summary>
public sealed class PhoneDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Token { get; set; } = "";

    /// <summary>Постоянный идентификатор приложения на телефоне. Пусто у записей до 1.4.0.</summary>
    public string Instance { get; set; } = "";

    /// <summary>
    /// Аппаратный идентификатор телефона (Android ID) или MAC Bluetooth. Не меняется
    /// при переустановке приложения, поэтому по нему одна установка телефона
    /// остаётся одной записью, даже если идентификатор установки сменился.
    /// </summary>
    public string Hardware { get; set; } = "";

    public DateTime PairedAt { get; set; }
    public DateTime? LastSeen { get; set; }

    /// <summary>Транспорт последнего подключения: «Bluetooth» или «Wi-Fi».</summary>
    public string LastTransport { get; set; } = "";

    public string Details
    {
        get
        {
            var seen = LastSeen is { } value
                ? value.ToLocalTime().ToString("dd MMM, HH:mm")
                : "ещё не подключался";
            var transport = string.IsNullOrEmpty(LastTransport) ? "" : $" ({LastTransport})";
            return $"Сопряжён {PairedAt.ToLocalTime():dd MMM yyyy}  ·  {seen}{transport}";
        }
    }
}

/// <summary>
/// Список сопряжённых телефонов в <c>%LOCALAPPDATA%\DynamicIsland\devices.bin</c>.
/// Формат — JSON, зашифрованный DPAPI тем же способом, что и ключи API.
/// </summary>
public sealed class PhoneDeviceStore
{
    private readonly string _path = Path.Combine(AppPaths.DataDirectory, "devices.bin");
    private readonly object _gate = new();
    private List<PhoneDevice> _devices = [];

    public PhoneDeviceStore()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var raw = SecretProtector.Unprotect(File.ReadAllText(_path));
            if (string.IsNullOrWhiteSpace(raw)) return;
            _devices = JsonSerializer.Deserialize<List<PhoneDevice>>(raw) ?? [];
            if (MergeDuplicates()) Save();
        }
        catch (Exception ex) { Diagnostics.Log("PhoneDeviceStore: загрузка не удалась — " + ex.Message); }
    }

    public IReadOnlyList<PhoneDevice> Devices
    {
        get { lock (_gate) return _devices.ToArray(); }
    }

    public PhoneDevice? FindByToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        lock (_gate) return _devices.FirstOrDefault(x => x.Token == token);
    }

    public PhoneDevice? FindById(string id)
    {
        lock (_gate) return _devices.FirstOrDefault(x => x.Id == id);
    }

    /// <summary>
    /// Заводит или находит запись устройства. Порядок поиска — от самого
    /// устойчивого признака: аппаратный идентификатор, идентификатор установки,
    /// ключ. Сканировать QR можно сколько угодно раз и переустанавливать
    /// приложение — запись останется одна.
    /// </summary>
    public PhoneDevice Adopt(string name, string instance, string hardware, string tokenUsed, out bool created)
    {
        lock (_gate)
        {
            PhoneDevice? byHardware = null;
            PhoneDevice? byInstance = null;
            PhoneDevice? byToken = null;
            if (!string.IsNullOrEmpty(hardware)) byHardware = _devices.FirstOrDefault(x => x.Hardware == hardware);
            if (!string.IsNullOrEmpty(instance)) byInstance = _devices.FirstOrDefault(x => x.Instance == instance);
            if (!string.IsNullOrEmpty(tokenUsed)) byToken = _devices.FirstOrDefault(x => x.Token == tokenUsed);

            // Записи до 1.4.0 не знали идентификатора установки: каждое сканирование
            // QR заводило новую копию с тем же именем. Принимаем такую запись за
            // прежнюю встречу, чтобы обновление не добавило ещё одну.
            var legacy = byHardware is null && byInstance is null && byToken is null && !string.IsNullOrWhiteSpace(name)
                ? _devices
                    .Where(x => string.IsNullOrEmpty(x.Instance) && string.IsNullOrEmpty(x.Hardware) &&
                                string.Equals(x.Name, name, StringComparison.Ordinal))
                    .OrderBy(x => x.PairedAt == default ? DateTime.MaxValue : x.PairedAt)
                    .FirstOrDefault()
                : null;

            var device = byHardware ?? byInstance ?? byToken ?? legacy;
            // Нашлись разные записи одного телефона — оставляем одну.
            foreach (var other in new[] { byHardware, byInstance, byToken }.Where(x => x is not null && x != device))
                Absorb(device!, other!);

            created = device is null;
            if (device is null)
            {
                device = new PhoneDevice
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = string.IsNullOrWhiteSpace(name) ? "Телефон" : name,
                    Token = NewToken(),
                    Instance = instance,
                    Hardware = hardware,
                    PairedAt = DateTime.UtcNow
                };
                _devices.Add(device);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(name)) device.Name = name;
                if (!string.IsNullOrEmpty(instance)) device.Instance = instance;
                if (!string.IsNullOrEmpty(hardware)) device.Hardware = hardware;
                // Запись без ключа (старый формат) должна получить постоянный ключ.
                if (string.IsNullOrEmpty(device.Token)) device.Token = NewToken();
            }

            if (!string.IsNullOrEmpty(instance) || !string.IsNullOrEmpty(hardware)) MergeDuplicates();
            Save();
            return device;
        }
    }

    public void MarkSeen(string id, string transport = "")
    {
        lock (_gate)
        {
            var device = _devices.FirstOrDefault(x => x.Id == id);
            if (device is null) return;
            device.LastSeen = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(transport)) device.LastTransport = transport;
            Save();
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var removed = _devices.RemoveAll(x => x.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _devices.Clear();
            Save();
        }
    }

    /// <summary>
    /// Сливает записи одного телефона в одну. Порядок признаков — как при
    /// поиске: сначала аппаратный идентификатор, потом идентификатор установки,
    /// затем имя (записи до 1.4.0 другого признака не имели).
    /// </summary>
    private bool MergeDuplicates()
    {
        var changed = false;

        var byHardware = _devices
            .Where(x => !string.IsNullOrEmpty(x.Hardware))
            .GroupBy(x => x.Hardware, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToArray();
        foreach (var group in byHardware)
        {
            var keep = group.OrderBy(x => x.PairedAt == default ? DateTime.MaxValue : x.PairedAt).First();
            foreach (var other in group.Where(x => x != keep).ToArray())
            {
                Absorb(keep, other);
                changed = true;
            }
        }

        var groups = _devices
            .Where(x => !string.IsNullOrEmpty(x.Instance))
            .GroupBy(x => x.Instance, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToArray();
        foreach (var group in groups)
        {
            var keep = group.OrderBy(x => x.PairedAt == default ? DateTime.MaxValue : x.PairedAt).First();
            foreach (var other in group.Where(x => x != keep).ToArray())
            {
                Absorb(keep, other);
                changed = true;
            }
        }

        var legacy = _devices
            .Where(x => string.IsNullOrEmpty(x.Instance) && string.IsNullOrEmpty(x.Hardware) &&
                        !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToArray();
        foreach (var group in legacy)
        {
            var keep = group.OrderBy(x => x.PairedAt == default ? DateTime.MaxValue : x.PairedAt).First();
            foreach (var other in group.Where(x => x != keep).ToArray())
            {
                Absorb(keep, other);
                changed = true;
            }
        }
        return changed;
    }

    private void Absorb(PhoneDevice keep, PhoneDevice other)
    {
        _devices.Remove(other);
        if (string.IsNullOrEmpty(keep.Name)) keep.Name = other.Name;
        if (keep.PairedAt == default || (other.PairedAt != default && other.PairedAt < keep.PairedAt))
            keep.PairedAt = other.PairedAt;
        if (other.LastSeen is { } seen && (keep.LastSeen is null || seen > keep.LastSeen))
        {
            keep.LastSeen = seen;
            if (!string.IsNullOrEmpty(other.LastTransport)) keep.LastTransport = other.LastTransport;
        }
        if (string.IsNullOrEmpty(keep.Instance)) keep.Instance = other.Instance;
        if (string.IsNullOrEmpty(keep.Hardware)) keep.Hardware = other.Hardware;
        // Ключ выжившей записи остаётся каноническим: телефон получит его в auth.ok.
        Diagnostics.Log($"PhoneDeviceStore: запись «{other.Name}» слита с «{keep.Name}»");
    }

    private static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var json = JsonSerializer.Serialize(_devices);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, SecretProtector.Protect(json));
            File.Move(temp, _path, true);
        }
        catch (Exception ex) { Diagnostics.Log("PhoneDeviceStore: сохранение не удалось — " + ex.Message); }
    }
}
