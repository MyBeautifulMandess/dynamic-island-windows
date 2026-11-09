using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

internal static class LocalStore<T>
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static List<T> Load(string fileName)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDirectory, fileName);
            if (!File.Exists(path)) return [];
            var raw = SecretProtector.Unprotect(File.ReadAllText(path));
            if (string.IsNullOrWhiteSpace(raw)) return [];
            return JsonSerializer.Deserialize<List<T>>(raw, Json) ?? [];
        }
        catch { return []; }
    }

    internal static void Save(string fileName, IEnumerable<T> items)
    {
        var path = Path.Combine(AppPaths.DataDirectory, fileName);
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var json = JsonSerializer.Serialize(items.ToArray(), Json);
        var temp = path + ".tmp";
        File.WriteAllText(temp, SecretProtector.Protect(json));
        File.Move(temp, path, true);
    }
}

public sealed class CalendarService
{
    public List<CalendarEventItem> Load()
    {
        var events = LocalStore<CalendarEventItem>.Load("calendar.dat");
        events.Sort((a, b) => a.Start.CompareTo(b.Start));
        return events.Where(x => x.End > DateTime.Today.AddMonths(-1) && x.Start < DateTime.Today.AddMonths(13)).ToList();
    }

    public void Save(IEnumerable<CalendarEventItem> events) =>
        LocalStore<CalendarEventItem>.Save("calendar.dat", events.OrderBy(x => x.Start).Take(500));
}

public sealed class ReminderService
{
    public List<ReminderItem> Load() =>
        LocalStore<ReminderItem>.Load("reminders.dat").OrderBy(x => x.Due).ToList();

    public void Save(IEnumerable<ReminderItem> reminders) =>
        LocalStore<ReminderItem>.Save("reminders.dat", reminders.Take(300));
}
