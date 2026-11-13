using System.Text.Json.Serialization;

namespace DynamicIsland.Models;

/// <summary>
/// Одна запись планера в том виде, в каком она ходит по каналу и лежит в локальном
/// зеркале. Событие и напоминание описаны одной структурой, чтобы слияние было
/// единым: у записи есть вид, время изменения и признак удаления.
/// </summary>
public sealed class PlannerRecord
{
    public const string EventKind = "event";
    public const string ReminderKind = "reminder";

    public string Id { get; set; } = "";
    public string Kind { get; set; } = EventKind;
    public string Title { get; set; } = "";

    /// <summary>Время изменения в миллисекундах UTC. По нему решается, чья версия новее.</summary>
    public long Updated { get; set; }

    /// <summary>Надгробие: запись удалена. Хранится, чтобы удаление не «воскресло» при слиянии.</summary>
    public bool Deleted { get; set; }

    // Событие
    public long Start { get; set; }
    public long End { get; set; }
    public bool AllDay { get; set; }
    public int ReminderMinutes { get; set; }
    public string ReminderId { get; set; } = "";

    // Напоминание
    public long Due { get; set; }
    public bool Done { get; set; }
    public long? CompletedAt { get; set; }
    public string Source { get; set; } = "";
    public string EventId { get; set; } = "";

    [JsonIgnore] public string Origin { get; set; } = "pc";
}

/// <summary>Изменение, которое нужно применить к коллекциям окна.</summary>
public sealed record PlannerChange(PlannerRecord Record);
