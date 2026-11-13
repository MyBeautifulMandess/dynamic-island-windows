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
    private DateTime _suppressUntil;
    private string _lastRemoteText = "";

    public event Action<IReadOnlyList<ClipboardItem>>? Changed;

    /// <summary>Текст, скопированный на этом компьютере и ожидающий отправки на телефон.</summary>
    public event Action<ClipboardItem>? RemoteChanged;

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

    /// <summary>Последний текст, пришедший с телефона: используется для защиты от петель.</summary>
    public string LastRemoteText => _lastRemoteText;

    public void Capture()
    {
        try
        {
            // Программная вставка принятого с телефона текста не должна
            // вернуться обратно как новое локальное копирование.
            if (DateTime.UtcNow < _suppressUntil) return;
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.Length > 20000) text = text[..20000];
            if (text == _lastRemoteText) return;
            if (_items.Count > 0 && _items[0].Text == text) return;
            _items.RemoveAll(x => x.Text == text);
            var item = new ClipboardItem(text, DateTime.Now);
            _items.Insert(0, item);
            if (_items.Count > Limit) _items.RemoveRange(Limit, _items.Count - Limit);
            Save();
            Changed?.Invoke(Items);
            RemoteChanged?.Invoke(item);
        }
        catch { }
    }

    /// <summary>
    /// Добавляет элемент, пришедший с телефона. Системный буфер не трогает:
    /// вставка выполняется отдельно, когда пользователь открывает панель.
    /// </summary>
    public ClipboardItem? AddRemote(string text, string origin)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length > 20000) text = text[..20000];
        _lastRemoteText = text;
        if (_items.Count > 0 && _items[0].Text == text) return null;
        _items.RemoveAll(x => x.Text == text);
        var item = new ClipboardItem(text, DateTime.Now) { Origin = origin };
        _items.Insert(0, item);
        if (_items.Count > Limit) _items.RemoveRange(Limit, _items.Count - Limit);
        Save();
        Changed?.Invoke(Items);
        return item;
    }

    /// <summary>
    /// Кладёт текст с телефона в системный буфер Windows, подавляя собственный
    /// перехват на 1,5 с. Так одно копирование даёт ровно один переход.
    /// </summary>
    public bool ApplyRemote(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length > 20000) text = text[..20000];
        _suppressUntil = DateTime.UtcNow.AddSeconds(1.5);
        _lastRemoteText = text;
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch
        {
            // Буфер может быть занят другим приложением — повторим один раз.
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch { return false; }
        }
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
