using System.IO;
using System.Text.Json;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>Сопряжённый телефон: постоянный ключ выдаётся после первого успешного QR-сопряжения.</summary>
public sealed class PhoneDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTime PairedAt { get; set; }

    /// <summary>Последнее подключение по этому ключу. Пусто — устройство ещё ни разу не подключалось.</summary>
    public DateTime? LastSeen { get; set; }

    public string Details
    {
        get
        {
            var seen = LastSeen is { } value
                ? value.ToLocalTime().ToString("dd MMM, HH:mm")
                : "ещё не подключался";
            return $"Сопряжён {PairedAt.ToLocalTime():dd MMM yyyy}  ·  {seen}";
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
        }
        catch (Exception ex) { Diagnostics.Log("PhoneDeviceStore: загрузка не удалась — " + ex.Message); }
    }

    public IReadOnlyList<PhoneDevice> Devices
    {
        get { lock (_gate) return _devices.ToArray(); }
    }

    public PhoneDevice? Find(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        lock (_gate) return _devices.FirstOrDefault(x => x.Token == token);
    }

    public PhoneDevice? FindById(string id)
    {
        lock (_gate) return _devices.FirstOrDefault(x => x.Id == id);
    }

    /// <summary>Записывает устройство, заменяя прежнюю запись с тем же ключом (перепáривание).</summary>
    public PhoneDevice Add(string name, string token)
    {
        lock (_gate)
        {
            var existing = _devices.FirstOrDefault(x => x.Token == token);
            if (existing is not null)
            {
                existing.Name = string.IsNullOrWhiteSpace(name) ? existing.Name : name;
                Save();
                return existing;
            }
            var device = new PhoneDevice
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? "Телефон" : name,
                Token = token,
                PairedAt = DateTime.UtcNow
            };
            _devices.Add(device);
            Save();
            return device;
        }
    }

    public void MarkSeen(string id)
    {
        lock (_gate)
        {
            var device = _devices.FirstOrDefault(x => x.Id == id);
            if (device is null) return;
            device.LastSeen = DateTime.UtcNow;
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
