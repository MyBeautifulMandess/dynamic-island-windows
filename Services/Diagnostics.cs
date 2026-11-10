using System.IO;

namespace DynamicIsland.Services;

internal static class Diagnostics
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(AppPaths.DataDirectory, "diagnostics.log");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                var file = new FileInfo(LogPath);
                if (file.Exists && file.Length > MaxBytes) file.Delete();
                File.AppendAllText(LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
