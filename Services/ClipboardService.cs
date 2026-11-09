using System.IO;
using System.Text.Json;
using System.Windows;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

public sealed class ClipboardService
{
    private const int Limit = 40;
    private readonly string _path = Path.Combine(AppPaths.DataDirectory, "clipboard.json");
    private readonly List<ClipboardItem> _items = [];

    public event Action<IReadOnlyList<ClipboardItem>>? Changed;

    public ClipboardService()
    {
        try
        {
            if (File.Exists(_path))
                _items.AddRange(JsonSerializer.Deserialize<List<ClipboardItem>>(File.ReadAllText(_path)) ?? []);
        }
        catch { }
    }

    public IReadOnlyList<ClipboardItem> Items => _items;

    public void Capture()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.Length > 20000) text = text[..20000];
            if (_items.Count > 0 && _items[0].Text == text) return;
            _items.RemoveAll(x => x.Text == text);
            _items.Insert(0, new ClipboardItem(text, DateTime.Now));
            if (_items.Count > Limit) _items.RemoveRange(Limit, _items.Count - Limit);
            Save();
            Changed?.Invoke(Items);
        }
        catch { }
    }

    public void Remove(ClipboardItem item)
    {
        _items.Remove(item);
        Save();
        Changed?.Invoke(Items);
    }

    public void Clear()
    {
        _items.Clear();
        Save();
        Changed?.Invoke(Items);
    }

    private void Save()
    {
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_items));
            File.Move(temp, _path, true);
        }
        catch { }
    }
}
