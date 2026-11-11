using System.Net.Http;
using System.Text.Json;

namespace DynamicIsland.Services;

public sealed record WeatherReading(string City, double Temperature, string Condition, string Details, string Icon);

public sealed class WeatherService
{
    // Один экземпляр на процесс: HttpClient не пересоздаётся, соединения
    // переиспользуются, а сокеты не остаются в TIME_WAIT.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<WeatherReading> GetCurrentAsync(string key, string city, string language, CancellationToken token = default)
    {
        var url = "https://api.openweathermap.org/data/2.5/weather?units=metric&appid=" +
                  Uri.EscapeDataString(key.Trim()) + "&q=" + Uri.EscapeDataString(city.Trim()) +
                  "&lang=" + Uri.EscapeDataString(language);
        using var response = await Http.GetAsync(url, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try
            {
                using var error = JsonDocument.Parse(body);
                if (error.RootElement.TryGetProperty("message", out var messageElement))
                    message = messageElement.GetString();
            }
            catch (JsonException) { }
            throw new InvalidOperationException(message is null
                ? $"OpenWeatherMap: {(int)response.StatusCode}"
                : $"OpenWeatherMap: {message}");
        }

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var weather = root.GetProperty("weather")[0];
        var main = root.GetProperty("main");
        var wind = root.GetProperty("wind");
        var sys = root.GetProperty("sys");
        var iconCode = weather.GetProperty("icon").GetString() ?? "";
        return new WeatherReading(
            $"{root.GetProperty("name").GetString()}, {sys.GetProperty("country").GetString()}",
            main.GetProperty("temp").GetDouble(),
            weather.GetProperty("description").GetString() ?? "",
            $"Ощущается как {main.GetProperty("feels_like").GetDouble():0.#}°  ·  Влажность {main.GetProperty("humidity").GetInt32()}%\n" +
            $"Ветер {wind.GetProperty("speed").GetDouble():0.#} м/с  ·  Давление {main.GetProperty("pressure").GetDouble():0} гПа",
            WeatherIcon(weather.GetProperty("id").GetInt32(), iconCode));
    }

    private static string WeatherIcon(int code, string iconCode) => code switch
    {
        200 or 201 or 202 or 210 or 211 or 212 or 221 or 230 or 231 or 232 => "ϟ",
        >= 300 and < 400 or >= 500 and < 600 => "☂",
        >= 600 and < 700 => "❄",
        >= 700 and < 800 => "≋",
        800 => iconCode.EndsWith("n", StringComparison.OrdinalIgnoreCase) ? "☾" : "☀",
        801 or 802 or 803 or 804 => "☁",
        _ => "☁"
    };
}
