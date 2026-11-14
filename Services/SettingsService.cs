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
                settings.AiKey = SecretProtector.Unprotect(settings.AiKey);
                if (string.IsNullOrWhiteSpace(settings.AiKey) && !string.IsNullOrWhiteSpace(settings.RouterAiKey))
                {
                    settings.AiKey = settings.RouterAiKey;
                    settings.AiModel = settings.RouterAiModel;
                    settings.AiBaseUrl = "https://routerai.ru/api/v1";
                    settings.AiProvider = "RouterAI";
                }
                if (Migrate(settings)) Save(settings);
                return settings;
            }
        }
        catch { }
        return new AppSettings();
    }

    /// <summary>
    /// Приведение старых настроек к нынешнему поведению. До ревизии 2 автозакрытие
    /// порта было включено по умолчанию, и у всех, кто хоть раз сохранил
    /// настройки, оно осталось включённым — телефон приходил на закрытый порт,
    /// и связь «то есть, то нет». Выключаем один раз; дальше решает пользователь.
    /// Возвращает true, если файл нужно перезаписать.
    /// </summary>
    private static bool Migrate(AppSettings settings)
    {
        if (settings.SettingsRevision >= AppSettings.CurrentRevision)
        {
            settings.SettingsRevision = AppSettings.CurrentRevision;
            return false;
        }
        settings.Phone.IdleShutdown = false;
        settings.SettingsRevision = AppSettings.CurrentRevision;
        return true;
    }

    public void Save(AppSettings settings)
    {
        var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();
        snapshot.RouterAiKey = SecretProtector.Protect(snapshot.RouterAiKey);
        snapshot.WeatherKey = SecretProtector.Protect(snapshot.WeatherKey);
        snapshot.AiKey = SecretProtector.Protect(snapshot.AiKey);
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
