using System.IO;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

public sealed class DownloadsService : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    private IReadOnlyList<DownloadItem> _items = [];
    private int _refreshing;

    public event Action<IReadOnlyList<DownloadItem>>? Changed;
    public string DownloadsPath { get; } = GetDownloadsPath();
    public string Status { get; private set; } = "";

    public DownloadsService()
    {
        _debounce = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            if (!Directory.Exists(DownloadsPath))
            {
                Status = "Папка загрузок не найдена";
                return;
            }
            _watcher = new FileSystemWatcher(DownloadsPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _watcher.Created += Schedule;
            _watcher.Changed += Schedule;
            _watcher.Renamed += Schedule;
            _watcher.Deleted += Schedule;
            Refresh();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    private void Schedule(object sender, FileSystemEventArgs e) => _debounce.Change(600, Timeout.Infinite);

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            if (!Directory.Exists(DownloadsPath)) return;
            var files = new DirectoryInfo(DownloadsPath).EnumerateFiles()
                .OrderByDescending(x => x.LastWriteTime)
                .Take(30)
                .Select(x =>
                {
                    try { return new DownloadItem(x.FullName, x.Name, x.Length, x.LastWriteTime); }
                    catch { return new DownloadItem(x.FullName, x.Name, 0, x.LastWriteTime); }
                }).ToArray();
            _items = files;
            Changed?.Invoke(files);
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    public IReadOnlyList<DownloadItem> Items => _items;

    private static string GetDownloadsPath()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path) == 0 && path != IntPtr.Zero)
            {
                var result = System.Runtime.InteropServices.Marshal.PtrToStringUni(path);
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(path);
                if (!string.IsNullOrWhiteSpace(result)) return result;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
