using System.Windows;
using System.Windows.Media;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

/// <summary>
/// Собирает палитру интерфейса из трёх настроек: фон, цвет текста и акцент.
/// Все остальные оттенки считаются от них, поэтому светлая и тёмная темы
/// выглядят одинаково аккуратно, а текст и иконки всегда контрастны фону.
/// </summary>
public static class ThemeService
{
    public static bool TryParse(string? value, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        try
        {
            if (ColorConverter.ConvertFromString(text) is not Color parsed) return false;
            color = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Color Mix(Color first, Color second, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(first.A + (second.A - first.A) * t),
            (byte)Math.Round(first.R + (second.R - first.R) * t),
            (byte)Math.Round(first.G + (second.G - first.G) * t),
            (byte)Math.Round(first.B + (second.B - first.B) * t));
    }

    public static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(Math.Round(color.A * alpha), 0, 255), color.R, color.G, color.B);

    /// <summary>
    /// Цвет с заданной прозрачностью: так поверхности сохраняют общую
    /// «полупрозрачность» фона и не выглядят плотнее него.
    /// </summary>
    private static Color Surface(Color color, Color background) =>
        Color.FromArgb(background.A, color.R, color.G, color.B);

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Применяет палитру к общим ресурсам приложения. Неверный цвет не
    /// применяется, остальные применяются как есть — так можно спокойно
    /// набирать HEX в поле, не сбрасывая введённое значение.
    /// </summary>
    public static bool Apply(AppSettings settings, out string invalidKey)
    {
        invalidKey = "";
        var valid = true;
        if (!TryParse(settings.BackgroundColor, out var background))
        {
            invalidKey = nameof(AppSettings.BackgroundColor);
            valid = false;
            background = Color.FromRgb(0x14, 0x14, 0x19);
        }
        if (!TryParse(settings.ForegroundColor, out var foreground))
        {
            invalidKey = nameof(AppSettings.ForegroundColor);
            valid = false;
            foreground = Color.FromRgb(0xF5, 0xF5, 0xF7);
        }
        if (!TryParse(settings.Accent, out var accent))
        {
            invalidKey = nameof(AppSettings.Accent);
            valid = false;
            accent = Color.FromRgb(0x8B, 0x7C, 0xFF);
        }

        var resources = Application.Current.Resources;

        void Set(string key, object value) => resources[key] = value;

        // Прозрачность фона задаётся и в HEX (8 цифр), и ползунком в настройках.
        var alpha = background.A / 255d * Math.Clamp(settings.Opacity, 0.3, 1);
        background = Color.FromArgb((byte)Math.Round(background.A * Math.Clamp(settings.Opacity, 0.3, 1)), background.R, background.G, background.B);
        var tint = alpha;

        Set("AccentBrush", Brush(accent));
        Set("AccentSoftBrush", Brush(WithAlpha(accent, 0.28 * tint)));
        Set("AccentMutedBrush", Brush(WithAlpha(accent, 0.16 * tint)));
        Set("OnAccentBrush", Brush(IsLight(accent) ? Color.FromRgb(0x14, 0x14, 0x19) : Colors.White));

        Set("TextBrush", Brush(foreground));
        Set("IconBrush", Brush(foreground));
        Set("MutedBrush", Brush(WithAlpha(foreground, 0.62)));
        Set("HintBrush", Brush(WithAlpha(foreground, 0.42)));

        var panelTop = Mix(background, foreground, 0.07);
        var panelBottom = Mix(background, foreground, 0.015);
        var gradient = new LinearGradientBrush(panelTop, panelBottom, new Point(0, 0), new Point(0, 1));
        gradient.Freeze();
        Set("PanelBrush", gradient);

        Set("CardBrush", Brush(Surface(Mix(background, foreground, 0.085), background)));
        Set("SoftBrush", Brush(WithAlpha(foreground, 0.07 * tint)));
        Set("FieldBrush", Brush(Surface(Mix(background, foreground, 0.045), background)));
        Set("SurfaceBrush", Brush(Surface(Mix(background, foreground, 0.13), background)));
        Set("StrokeBrush", Brush(WithAlpha(foreground, 0.16 * tint)));
        Set("DividerBrush", Brush(WithAlpha(foreground, 0.11 * tint)));
        Set("HoverBrush", Brush(WithAlpha(foreground, 0.09 * tint)));
        Set("PressBrush", Brush(WithAlpha(foreground, 0.16 * tint)));
        Set("TrackBrush", Brush(WithAlpha(foreground, 0.16 * tint)));
        Set("DangerBrush", Brush(Mix(foreground, Color.FromRgb(0xFF, 0x45, 0x3A), 0.8)));
        Set("SuccessBrush", Brush(Color.FromRgb(0x4C, 0xD0, 0x7D)));
        return valid;
    }

    public static bool IsLight(Color color)
    {
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255d;
        return luminance > 0.62;
    }
}
