using System.IO;
using System.Text.Json;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

public sealed class NotesService
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    public NotesService()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        _path = Path.Combine(AppPaths.DataDirectory, "notes.dat");
    }

    public IReadOnlyList<NoteItem> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var raw = SecretProtector.Unprotect(File.ReadAllText(_path));
            if (string.IsNullOrWhiteSpace(raw)) return [];
            return JsonSerializer.Deserialize<List<NoteItem>>(raw, _json) ?? [];
        }
        catch { return []; }
    }

    public void Save(IEnumerable<NoteItem> notes)
    {
        var json = JsonSerializer.Serialize(notes.Take(200).ToArray(), _json);
        var protectedData = SecretProtector.Protect(json);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, protectedData);
        File.Move(temp, _path, true);
    }
}
