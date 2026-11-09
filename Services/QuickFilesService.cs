using System.IO;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

public sealed class QuickFilesService : IDisposable
{
    private readonly Func<string> _folderProvider;
    private readonly Timer _debounce;
    private FileSystemWatcher? _watcher;
    private string _folder = "";
    private IReadOnlyList<DownloadItem> _items = [];
    private int _refreshing;
    private bool _disposed;

    public event Action<IReadOnlyList<DownloadItem>>? Changed;
    public string Status { get; private set; } = "";
    public string Folder => _folder;

    public QuickFilesService(Func<string> folderProvider)
    {
        _folderProvider = folderProvider;
        _debounce = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Refresh()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var folder = _folderProvider().Trim();
            if (folder != _folder) ConfigureWatcher(folder);
            if (string.IsNullOrWhiteSpace(folder))
            {
                Status = "Папка не выбрана";
                _items = [];
                Changed?.Invoke(_items);
                return;
            }
            if (!Directory.Exists(folder))
            {
                Status = "Папка не найдена";
                _items = [];
                Changed?.Invoke(_items);
                return;
            }
            _items = new DirectoryInfo(folder).EnumerateFiles()
                .OrderByDescending(x => x.LastWriteTime)
                .Take(50)
                .Select(x => new DownloadItem(x.FullName, x.Name, SafeLength(x), x.LastWriteTime))
                .ToArray();
            Status = $"{_items.Count} файлов";
            Changed?.Invoke(_items);
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    private void ConfigureWatcher(string folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        _folder = folder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _watcher.Created += Schedule;
            _watcher.Changed += Schedule;
            _watcher.Renamed += Schedule;
            _watcher.Deleted += Schedule;
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    public void ReloadFolder()
    {
        _folder = "";
        Refresh();
    }

    private void Schedule(object sender, FileSystemEventArgs e) => _debounce.Change(600, Timeout.Infinite);

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; } catch { return 0; }
    }

    public IReadOnlyList<DownloadItem> Items => _items;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
