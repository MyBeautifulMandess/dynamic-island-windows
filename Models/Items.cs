using System.Windows.Media;

namespace DynamicIsland.Models;

public sealed record NotificationItem(uint Id, string AppName, string Title, string Body, DateTime Created)
{
    public string Time => Created.ToString("HH:mm");
    public string Initial => string.IsNullOrWhiteSpace(AppName) ? "•" : AppName[..1].ToUpperInvariant();
}

public sealed record ClipboardItem(string Text, DateTime Created)
{
    public string Preview => Text.Replace("\r", " ").Replace("\n", " ").Trim();
    public string Time => Created.ToString("HH:mm");
}

public sealed record DownloadItem(string Path, string Name, long Size, DateTime Created)
{
    public string Extension => System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "FILE";
    public string Details => $"{FormatSize(Size)}  ·  {Created:dd MMM, HH:mm}";
    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / 1073741824d:0.0} ГБ",
        >= 1024 * 1024 => $"{bytes / 1048576d:0.0} МБ",
        >= 1024 => $"{bytes / 1024d:0.0} КБ",
        _ => $"{bytes} Б"
    };
}

public sealed class ChatMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public bool IsUser => Role == "user";
}

public sealed class MediaState : ObservableObject
{
    private string _title = "Ничего не воспроизводится";
    private string _artist = "Медиа появится здесь";
    private string _source = "";
    private bool _isPlaying;
    private bool _hasMedia;
    private ImageSource? _artwork;
    private double _position;
    private double _duration = 1;
    private string _positionText = "0:00";
    private string _durationText = "0:00";

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Artist { get => _artist; set => Set(ref _artist, value); }
    public string Source { get => _source; set => Set(ref _source, value); }
    public bool IsPlaying { get => _isPlaying; set { if (Set(ref _isPlaying, value)) Notify(nameof(PlayPauseGlyph)); } }
    public bool HasMedia { get => _hasMedia; set => Set(ref _hasMedia, value); }
    public ImageSource? Artwork { get => _artwork; set => Set(ref _artwork, value); }
    public double Position { get => _position; set => Set(ref _position, value); }
    public double Duration { get => _duration; set => Set(ref _duration, Math.Max(1, value)); }
    public string PositionText { get => _positionText; set => Set(ref _positionText, value); }
    public string DurationText { get => _durationText; set => Set(ref _durationText, value); }
    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE768";
}

public sealed class WeatherInfo : ObservableObject
{
    private string _city = "Погода";
    private double? _temperatureValue;
    private string _condition = "Добавьте ключ OpenWeatherMap в настройках";
    private string _details = "";
    private string _icon = "☀";
    private string _updated = "";
    private DateTime? _fetchedAt;

    public string City { get => _city; set => Set(ref _city, value); }

    /// <summary>
    /// Температура из ответа API. Строка для интерфейса вычисляется из неё, поэтому
    /// подписка обновляется при любом изменении значения, а не только тогда, когда
    /// совпала отформатированная строка.
    /// </summary>
    public double? TemperatureValue
    {
        get => _temperatureValue;
        set { if (Set(ref _temperatureValue, value)) { Notify(nameof(Temperature)); Notify(nameof(TemperatureText)); } }
    }

    public string Temperature => TemperatureValue is { } value ? FormatDegrees(value) : "—°";
    public string TemperatureText => TemperatureValue is { } value ? $"{FormatDegrees(value)}C" : "н/д";
    public string Condition { get => _condition; set => Set(ref _condition, value); }
    public string Details { get => _details; set => Set(ref _details, value); }
    public string Icon { get => _icon; set => Set(ref _icon, value); }
    public string Updated { get => _updated; set => Set(ref _updated, value); }
    public DateTime? FetchedAt { get => _fetchedAt; set => Set(ref _fetchedAt, value); }

    private static string FormatDegrees(double value)
    {
        var rounded = Math.Round(value, 1);
        if (rounded == 0) rounded = 0;
        return rounded.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + "°";
    }
}

public sealed record AgentPreset(string Name, string Description, string Prompt)
{
    public static readonly AgentPreset[] Defaults =
    [
        new("Ассистент", "Быстрые ответы и повседневные задачи", "Ты полезный русскоязычный ассистент. Отвечай кратко и по делу."),
        new("Разработчик", "Код, отладка и архитектура", "Ты опытный разработчик. Давай рабочие решения, поясняй важные компромиссы и не выдумывай API."),
        new("Аналитик", "Структурирование и исследование", "Ты аналитик. Разбивай сложные вопросы на части, отделяй факты от предположений и делай практические выводы."),
        new("Переводчик", "Естественный перевод RU ↔ EN", "Определи язык текста и переведи его: русский — на английский, остальные языки — на русский. Сохраняй смысл и форматирование, выводи только перевод.")
    ];
}
