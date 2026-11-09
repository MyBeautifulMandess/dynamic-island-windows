using System.IO;
using System.Text.Json;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

public sealed class SettingsService
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public SettingsService()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        _path = Path.Combine(AppPaths.DataDirectory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
                settings.RouterAiKey = SecretProtector.Unprotect(settings.RouterAiKey);
                settings.WeatherKey = SecretProtector.Unprotect(settings.WeatherKey);
                return settings;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();
        snapshot.RouterAiKey = SecretProtector.Protect(snapshot.RouterAiKey);
        snapshot.WeatherKey = SecretProtector.Protect(snapshot.WeatherKey);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, _json));
        File.Move(temp, _path, true);
    }
}

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland");
}
