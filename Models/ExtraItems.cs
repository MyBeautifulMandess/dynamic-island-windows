using System.Text.Json.Serialization;
using System.Windows.Media;

namespace DynamicIsland.Models;

public sealed record NoteItem(string Id, string Text, DateTime Created)
{
    public string Preview => Text.Replace("\r", " ").Replace("\n", " ").Trim();
    public string Time => Created.ToString("dd MMM, HH:mm");
}

public sealed class CalendarEventItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool IsAllDay { get; set; }
    public int ReminderMinutes { get; set; }
    public string ReminderId { get; set; } = "";

    [JsonIgnore]
    public string WhenText
    {
        get
        {
            var date = Start.Date;
            var today = DateTime.Today;
            var prefix = date == today ? "Сегодня" : date == today.AddDays(1) ? "Завтра" : Start.ToString("dd MMM");
            return IsAllDay ? $"{prefix} · весь день" : $"{prefix}, {Start:HH:mm}";
        }
    }

    [JsonIgnore] public string EndText => IsAllDay ? "" : $"до {End:HH:mm}";
    [JsonIgnore] public bool HasReminder => ReminderMinutes > 0;
    [JsonIgnore] public string ReminderText => ReminderMinutes > 0 ? $"Напоминание за {ReminderMinutes} мин" : "";
}

public sealed class ReminderItem : ObservableObject
{
    private string _title = "";
    private DateTime _due;
    private bool _done;
    private DateTime? _completedAt;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Source { get; set; } = "";
    public string EventId { get; set; } = "";
    public string Title { get => _title; set { if (Set(ref _title, value)) Notify(nameof(WhenText)); } }
    public DateTime Due { get => _due; set { if (Set(ref _due, value)) Notify(nameof(WhenText)); } }
    public bool Done { get => _done; set { if (Set(ref _done, value)) Notify(nameof(DoneText)); } }
    public DateTime? CompletedAt { get => _completedAt; set { if (Set(ref _completedAt, value)) Notify(nameof(DoneText)); } }

    [JsonIgnore]
    public string WhenText
    {
        get
        {
            var date = Due.Date;
            var today = DateTime.Today;
            var prefix = date == today ? "Сегодня" : date == today.AddDays(1) ? "Завтра" : Due.ToString("dd MMM");
            return $"{prefix}, {Due:HH:mm}";
        }
    }

    [JsonIgnore] public string DoneText => Done ? $"Выполнено · {(CompletedAt ?? Due):dd MMM, HH:mm}" : WhenText;
}

public sealed class CalendarDayItem
{
    public required DateTime Date { get; init; }
    public string Day => Date.Day.ToString();
    public bool HasItems { get; init; }
    public double Opacity { get; init; } = 1;
    public Brush Background { get; init; } = Brushes.Transparent;
    public Brush Foreground { get; init; } = Brushes.White;
    public string ToolTip { get; init; } = "";
}

public sealed class PanelOption : ObservableObject
{
    private bool _isVisible;
    private string _title = "";

    public required string Id { get; init; }
    public string Title { get => _title; set => Set(ref _title, value); }
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }
}

public sealed class QuickFileItem : ObservableObject
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Details { get; init; }
    public required string Extension { get; init; }
    public ImageSource? Icon { get; init; }
}

public sealed class TimeSlot : ObservableObject
{
    private bool _isSelected;

    public TimeSlot(TimeSpan time) => Time = time;

    public TimeSpan Time { get; }
    public string Text => $"{Time:hh\\:mm}";
    public bool IsSelected { get => _isSelected; set { if (Set(ref _isSelected, value)) { Notify(nameof(Background)); Notify(nameof(Foreground)); } } }
    public Brush Background => IsSelected ? AccentBrushValue : Brushes.Transparent;
    public Brush Foreground => IsSelected ? OnAccentBrushValue : TextBrushValue;

    private static Brush AccentBrushValue =>
        System.Windows.Application.Current?.Resources["AccentBrush"] as Brush ?? Brushes.Transparent;

    private static Brush OnAccentBrushValue =>
        System.Windows.Application.Current?.Resources["OnAccentBrush"] as Brush ?? Brushes.White;

    private static Brush TextBrushValue =>
        System.Windows.Application.Current?.Resources["TextBrush"] as Brush ?? Brushes.White;
}
