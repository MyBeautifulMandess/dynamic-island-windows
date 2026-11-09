namespace DynamicIsland.Models;

public static class PanelCatalog
{
    public static readonly (string Id, string Title, string Glyph)[] All =
    [
        ("home", "Центр", "\uE80F"),
        ("calendar", "Календарь", "\uE787"),
        ("reminders", "Напоминания", "\uEA8F"),
        ("notes", "Заметки", "\uE70B"),
        ("quickfiles", "Быстрые файлы", "\uE8B7"),
        ("media", "Медиа", "\uE8B2"),
        ("notifications", "Уведомления", "\uE7E7"),
        ("ai", "AI-агенты", "\uE99A"),
        ("clipboard", "Буфер", "\uE8C8"),
        ("files", "Загрузки", "\uE896"),
        ("weather", "Погода", "\uE706")
    ];

    public static string DefaultOrder { get; } = string.Join(',', All.Select(x => x.Id));
    public static string Title(string id) => All.FirstOrDefault(x => x.Id == id).Title ?? id;
    public static string Glyph(string id) => All.FirstOrDefault(x => x.Id == id).Glyph ?? "\uE8B7";
}
