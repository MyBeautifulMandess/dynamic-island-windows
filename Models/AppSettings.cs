namespace DynamicIsland.Models;

public sealed class AppSettings : ObservableObject
{
    private double _islandWidth = 278;
    private double _islandHeight = 42;
    private double _expandedWidth = 820;
    private double _expandedHeight = 540;
    private double _cornerRadius = 21;
    private double _opacity = 0.96;
    private double _horizontalOffset;
    private string _accent = "#8B7CFF";
    private string _screenMode = "cursor";
    private bool _hotCornerEnabled = true;
    private bool _launchAtStartup;
    private bool _showMediaPopup = true;
    private int _notificationSeconds = 6;
    private string _routerAiKey = "";
    private string _routerAiModel = "openai/gpt-chat-latest";
    private string _weatherKey = "";
    private string _weatherCity = "Moscow";
    private string _weatherLanguage = "ru";

    public double IslandWidth { get => _islandWidth; set => Set(ref _islandWidth, Math.Clamp(value, 210, 420)); }
    public double IslandHeight { get => _islandHeight; set => Set(ref _islandHeight, Math.Clamp(value, 34, 58)); }
    public double ExpandedWidth { get => _expandedWidth; set => Set(ref _expandedWidth, Math.Clamp(value, 640, 1100)); }
    public double ExpandedHeight { get => _expandedHeight; set => Set(ref _expandedHeight, Math.Clamp(value, 420, 700)); }
    public double CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, Math.Clamp(value, 8, 30)); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.7, 1)); }
    public double HorizontalOffset { get => _horizontalOffset; set => Set(ref _horizontalOffset, Math.Clamp(value, -500, 500)); }
    public string Accent { get => _accent; set => Set(ref _accent, value); }
    public string ScreenMode { get => _screenMode; set => Set(ref _screenMode, value); }
    public bool HotCornerEnabled { get => _hotCornerEnabled; set => Set(ref _hotCornerEnabled, value); }
    public bool LaunchAtStartup { get => _launchAtStartup; set => Set(ref _launchAtStartup, value); }
    public bool ShowMediaPopup { get => _showMediaPopup; set => Set(ref _showMediaPopup, value); }
    public int NotificationSeconds { get => _notificationSeconds; set => Set(ref _notificationSeconds, Math.Clamp(value, 3, 15)); }
    public string RouterAiKey { get => _routerAiKey; set => Set(ref _routerAiKey, value); }
    public string RouterAiModel { get => _routerAiModel; set => Set(ref _routerAiModel, value); }
    public string WeatherKey { get => _weatherKey; set => Set(ref _weatherKey, value); }
    public string WeatherCity { get => _weatherCity; set => Set(ref _weatherCity, value); }
    public string WeatherLanguage { get => _weatherLanguage; set => Set(ref _weatherLanguage, value); }
}
