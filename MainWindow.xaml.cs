using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Models;
using DynamicIsland.Services;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace DynamicIsland;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly NotificationService _notifications = new();
    private readonly MediaService _media = new();
    private readonly SystemControlService _system = new();
    private readonly ClipboardService _clipboard = new();
    private readonly DownloadsService _downloads = new();
    private readonly AiService _ai = new();
    private readonly WeatherService _weather = new();
    private readonly DispatcherTimer _cursorTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private readonly DispatcherTimer _toastTimer = new();
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _brightnessTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly List<FrameworkElement> _panels = [];
    private IntPtr _windowHandle;
    private IntPtr _previousForeground;
    private CancellationTokenSource? _weatherCts;
    private CancellationTokenSource? _chatCts;
    private bool _expanded;
    private bool _peekVisible;
    private bool _toastVisible;
    private bool _ready;
    private bool _closing;
    private bool _systemUpdating;
    private bool _seeking;
    private string _currentTab = "home";

    public AppSettings Settings { get; }
    public Models.MediaState Media { get; } = new();
    public WeatherInfo Weather { get; } = new();
    public ObservableCollection<NotificationItem> Notifications { get; } = [];
    public ObservableCollection<ClipboardItem> ClipboardItems { get; } = [];
    public ObservableCollection<DownloadItem> Downloads { get; } = [];
    public ObservableCollection<ChatMessage> Chat { get; } = [];
    public ObservableCollection<AgentPreset> Agents { get; } = [];
    public ObservableCollection<string> Models { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        Settings = _settingsService.Load();
        DataContext = this;
        foreach (var agent in AgentPreset.Defaults) Agents.Add(agent);
        AgentCombo.SelectedIndex = 0;
        RouterKeyInput.Password = Settings.RouterAiKey;
        WeatherKeyInput.Password = Settings.WeatherKey;
        ScreenModeCombo.SelectedIndex = Settings.ScreenMode == "primary" ? 1 : 0;
        _panels.AddRange([HomePanel, MediaPanel, NotificationsPanel, AiPanel, ClipboardPanel, FilesPanel, WeatherPanel, SettingsPanel]);

        _notifications.Changed += items => Dispatcher.Invoke(() => UpdateNotifications(items));
        _notifications.Received += item => Dispatcher.Invoke(() => ShowToast(item));
        _media.Changed += snapshot => Dispatcher.Invoke(() => ApplyMedia(snapshot));
        _media.TrackChanged += snapshot => Dispatcher.Invoke(() =>
        {
            if (Settings.ShowMediaPopup)
                ShowToast(new NotificationItem(0, snapshot.Source, snapshot.Title, snapshot.Artist, DateTime.Now));
        });
        _clipboard.Changed += _ => Dispatcher.Invoke(SyncClipboard);
        _downloads.Changed += items => Dispatcher.Invoke(() => Sync(Downloads, items));
        _system.RadioChanged += () => Dispatcher.Invoke(UpdateRadioStates);
        Settings.PropertyChanged += Settings_PropertyChanged;

        _cursorTimer.Tick += Cursor_Tick;
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); if (!IsMouseOver) Collapse(); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Collapse(); };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
        _weatherTimer.Tick += async (_, _) => await RefreshWeatherAsync(false);
        _mediaTimer.Tick += (_, _) => AdvanceMediaPosition();
        _brightnessTimer.Tick += async (_, _) =>
        {
            _brightnessTimer.Stop();
            await _system.SetBrightnessAsync((int)BrightnessSlider.Value);
        };

        SyncClipboard();
        Sync(Downloads, _downloads.Items);
        UpdateClock();
        ApplyAppearance();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_windowHandle);
        source?.AddHook(WindowHook);
        NativeMethods.AddClipboardFormatListener(_windowHandle);
        Collapse(false);
        _cursorTimer.Start();
        _clockTimer.Start();
        if (!string.IsNullOrWhiteSpace(Settings.WeatherKey)) _weatherTimer.Start();
        _ready = true;
        UpdateRadioStates();
        UpdateSystemControls();
        await Task.WhenAll(_media.InitializeAsync(), _system.InitializeAsync(), _notifications.InitializeAsync());
        Dispatcher.Invoke(() =>
        {
            NotificationStatus.Text = _notifications.Status;
            SystemStatus.Text = string.IsNullOrWhiteSpace(_system.Status) ? "Системные интерфейсы готовы" : _system.Status;
            UpdateRadioStates();
        });
        _brightnessTimer.Stop();
        var brightness = await _system.GetBrightnessAsync();
        _systemUpdating = true;
        BrightnessSlider.IsEnabled = brightness.HasValue;
        if (brightness.HasValue) BrightnessSlider.Value = brightness.Value;
        BrightnessText.Text = brightness.HasValue ? $"{brightness}%" : "Н/Д";
        _systemUpdating = false;
    }

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmClipboardUpdate) _clipboard.Capture();
        if (msg == NativeMethods.WmDpiChanged) Dispatcher.BeginInvoke(PositionWindow);
        return IntPtr.Zero;
    }

    private void Cursor_Tick(object? sender, EventArgs e)
    {
        if (_expanded || _toastVisible || _closing || !NativeMethods.GetCursorPos(out var point)) return;
        if (!Settings.HotCornerEnabled)
        {
            if (_peekVisible) Collapse();
            return;
        }
        var monitor = NativeMethods.GetMonitor(point);
        if (_peekVisible)
        {
            if (!IsMouseOver && point.Y > monitor.Work.Top + 6) Collapse();
            return;
        }
        if (point.Y <= monitor.Work.Top + 2 && point.Y >= monitor.Monitor.Top)
            ShowPeek();
    }

    private void ShowPeek()
    {
        if (_expanded || _toastVisible || _closing) return;
        _peekVisible = true;
        _collapseTimer.Stop();
        ExpandedPanel.Visibility = Visibility.Collapsed;
        ToastPanel.Visibility = Visibility.Collapsed;
        CompactPanel.Visibility = Visibility.Visible;
        Width = Settings.IslandWidth;
        Height = Settings.IslandHeight;
        PositionWindow();
        if (!IsVisible) Show();
    }

    private void Expand(string tab)
    {
        if (_closing) return;
        _collapseTimer.Stop();
        _toastTimer.Stop();
        if (!_expanded)
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != _windowHandle) _previousForeground = foreground;
        }
        _expanded = true;
        _peekVisible = false;
        _toastVisible = false;
        ToastPanel.Visibility = Visibility.Collapsed;
        CompactPanel.Visibility = Visibility.Collapsed;
        ExpandedPanel.Visibility = Visibility.Visible;
        var monitor = GetTargetMonitor();
        var scale = GetMonitorScale(monitor.handle);
        var availableWidth = (monitor.info.Work.Right - monitor.info.Work.Left) / scale - 20;
        var availableHeight = (monitor.info.Work.Bottom - monitor.info.Work.Top) / scale - 12;
        Width = Math.Min(Settings.ExpandedWidth, availableWidth);
        Height = Math.Min(Settings.ExpandedHeight, availableHeight);
        SelectTab(tab);
        PositionWindow();
        if (!IsVisible) Show();
        _mediaTimer.Stop();
        if (_currentTab == "media" && Media.IsPlaying) _mediaTimer.Start();
    }

    private void Collapse(bool animate = true)
    {
        if (_closing) return;
        _expanded = false;
        _peekVisible = false;
        _toastVisible = false;
        _collapseTimer.Stop();
        _toastTimer.Stop();
        _mediaTimer.Stop();
        ExpandedPanel.Visibility = Visibility.Collapsed;
        ToastPanel.Visibility = Visibility.Collapsed;
        CompactPanel.Visibility = Visibility.Visible;
        Width = Settings.IslandWidth;
        Height = Settings.IslandHeight;
        PositionWindow();
        if (IsVisible) Hide();
    }

    private void PositionWindow()
    {
        if (_closing) return;
        var target = GetTargetMonitor();
        var scale = GetMonitorScale(target.handle);
        var workWidth = (target.info.Work.Right - target.info.Work.Left) / scale;
        Left = target.info.Work.Left / scale + (workWidth - Width) / 2 + Settings.HorizontalOffset;
        Top = target.info.Work.Top / scale;
    }

    private (NativeMethods.MonitorInfo info, IntPtr handle) GetTargetMonitor()
    {
        NativeMethods.Point point;
        if (Settings.ScreenMode == "primary") point = new NativeMethods.Point { X = 0, Y = 0 };
        else if (!NativeMethods.GetCursorPos(out point)) point = new NativeMethods.Point { X = 0, Y = 0 };
        var handle = NativeMethods.MonitorFromPoint(point, NativeMethods.MonDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        NativeMethods.GetMonitorInfo(handle, ref info);
        return (info, handle);
    }

    private static double GetMonitorScale(IntPtr monitor)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0) return dpi / 96d;
        }
        catch { }
        return 1;
    }

    private void ShowToast(NotificationItem item)
    {
        if (_expanded || _closing) return;
        _peekVisible = false;
        _toastVisible = true;
        ToastInitial.Text = item.Initial;
        ToastApp.Text = item.AppName;
        ToastTime.Text = item.Time;
        ToastTitle.Text = item.Title;
        ToastBody.Text = item.Body;
        CompactPanel.Visibility = Visibility.Collapsed;
        ExpandedPanel.Visibility = Visibility.Collapsed;
        ToastPanel.Visibility = Visibility.Visible;
        Width = 440;
        Height = 126;
        PositionWindow();
        if (!IsVisible) Show();
        _toastTimer.Interval = TimeSpan.FromSeconds(Settings.NotificationSeconds);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void SelectTab(string tab)
    {
        _currentTab = tab;
        var names = new Dictionary<string, string>
        {
            ["home"] = "Центр", ["media"] = "Мультимедиа", ["notifications"] = "Уведомления", ["ai"] = "AI-агенты",
            ["clipboard"] = "Буфер", ["files"] = "Загрузки", ["weather"] = "Погода", ["settings"] = "Настройки"
        };
        HeaderContext.Text = "  /  " + names.GetValueOrDefault(tab, "Центр");
        foreach (var panel in _panels) panel.Visibility = Visibility.Collapsed;
        FrameworkElement selected = tab switch
        {
            "media" => MediaPanel, "notifications" => NotificationsPanel, "ai" => AiPanel, "clipboard" => ClipboardPanel,
            "files" => FilesPanel, "weather" => WeatherPanel, "settings" => SettingsPanel, _ => HomePanel
        };
        selected.Visibility = Visibility.Visible;
        if (tab != "ai" && SendButton.IsEnabled) AgentStatus.Text = "";
        if (tab == "media" && Media.IsPlaying) _mediaTimer.Start();
        else _mediaTimer.Stop();
        if (tab == "settings")
        {
            NotificationStatus.Text = _notifications.Status;
            SystemStatus.Text = string.IsNullOrWhiteSpace(_system.Status) ? "Системные интерфейсы готовы" : _system.Status;
        }
        if (tab == "weather" && Weather.Temperature == "—°") _ = RefreshWeatherAsync(false);
    }

    private void Nav_Click(object sender, RoutedEventArgs e) => SelectTab((string)((Button)sender).Tag);
    private void ShowNotifications_Click(object sender, RoutedEventArgs e) => SelectTab("notifications");
    private void CompactMedia_Click(object sender, RoutedEventArgs e) { Expand("media"); e.Handled = true; }
    private void Compact_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Expand("home");
        e.Handled = true;
    }
    private void Island_MouseEnter(object sender, MouseEventArgs e) => _collapseTimer.Stop();
    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        if ((_expanded || _peekVisible) && !_closing) { _collapseTimer.Stop(); _collapseTimer.Start(); }
    }
    private void Collapse_Click(object sender, RoutedEventArgs e) => Collapse();
    private void Toast_Click(object sender, RoutedEventArgs e) => Expand("notifications");
    private void Window_Activated(object sender, EventArgs e) => _collapseTimer.Stop();

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm");
        ExpandedClock.Text = now.ToString("HH:mm");
        DateText.Text = now.ToString("dddd, d MMMM");
        var battery = SystemControlService.GetBattery();
        BatteryText.Text = battery.Available ? $"{(battery.Charging ? "ϟ " : "")}{battery.Percent}%" : "";
    }

    private void ApplyMedia(MediaSnapshot snapshot)
    {
        Media.Title = snapshot.Title;
        Media.Artist = string.IsNullOrWhiteSpace(snapshot.Artist) ? "Неизвестный исполнитель" : snapshot.Artist;
        Media.Source = snapshot.Source;
        Media.HasMedia = snapshot.HasMedia;
        Media.IsPlaying = snapshot.IsPlaying;
        Media.Artwork = snapshot.Artwork;
        Media.Duration = snapshot.Duration;
        if (!_seeking) Media.Position = snapshot.Position;
        Media.PositionText = FormatTime(snapshot.Position);
        Media.DurationText = FormatTime(snapshot.Duration);
        CompactTrack.Text = snapshot.HasMedia ? snapshot.Title : "";
        CompactLabel.Visibility = snapshot.HasMedia ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private void AdvanceMediaPosition()
    {
        if (!_seeking && Media.IsPlaying && Media.Position < Media.Duration)
        {
            Media.Position++;
            Media.PositionText = FormatTime(Media.Position);
        }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();
    private void Seek_Down(object sender, MouseButtonEventArgs e) => _seeking = true;
    private async void Seek_Up(object sender, MouseButtonEventArgs e)
    {
        _seeking = false;
        await _media.SeekAsync(MediaSeek.Value);
    }

    private void UpdateNotifications(IReadOnlyList<NotificationItem> items)
    {
        Sync(Notifications, items);
        NotificationStatus.Text = _notifications.Status;
        NoNotificationsHome.Visibility = Notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshNotifications_Click(object sender, RoutedEventArgs e) => await _notifications.RefreshAsync(false);
    private void DismissNotification_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is NotificationItem item) _notifications.Dismiss(item.Id);
    }

    private void UpdateRadioStates()
    {
        _systemUpdating = true;
        WifiToggle.IsChecked = _system.GetRadioState(RadioKind.WiFi);
        BluetoothToggle.IsChecked = _system.GetRadioState(RadioKind.Bluetooth);
        WifiToggle.IsEnabled = WifiToggle.IsChecked.HasValue;
        BluetoothToggle.IsEnabled = BluetoothToggle.IsChecked.HasValue;
        _systemUpdating = false;
    }

    private void UpdateSystemControls()
    {
        _systemUpdating = true;
        VolumeSlider.Value = _system.GetVolume();
        MuteToggle.IsChecked = _system.GetMute();
        VolumeText.Text = $"{VolumeSlider.Value:0}%";
        _systemUpdating = false;
    }

    private async void Wifi_Click(object sender, RoutedEventArgs e)
    {
        if (!_systemUpdating) await _system.SetRadioStateAsync(RadioKind.WiFi, WifiToggle.IsChecked == true);
    }

    private async void Bluetooth_Click(object sender, RoutedEventArgs e)
    {
        if (!_systemUpdating) await _system.SetRadioStateAsync(RadioKind.Bluetooth, BluetoothToggle.IsChecked == true);
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (!_systemUpdating) _system.SetMute(MuteToggle.IsChecked == true);
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _systemUpdating) return;
        VolumeText.Text = $"{e.NewValue:0}%";
        _system.SetVolume(e.NewValue);
    }

    private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _systemUpdating) return;
        BrightnessText.Text = $"{e.NewValue:0}%";
        _brightnessTimer.Stop();
        _brightnessTimer.Start();
    }

    private void SyncClipboard() => Sync(ClipboardItems, _clipboard.Items);

    private async void PasteClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not ClipboardItem item) return;
        try { Clipboard.SetText(item.Text); } catch { return; }
        Collapse();
        if (_previousForeground != IntPtr.Zero) NativeMethods.SetForegroundWindow(_previousForeground);
        await Task.Delay(180);
        NativeMethods.SendPaste();
    }

    private void ClearClipboard_Click(object sender, RoutedEventArgs e) => _clipboard.Clear();

    private void RefreshFiles_Click(object sender, RoutedEventArgs e) => _downloads.Refresh();
    private void OpenDownloads_Click(object sender, RoutedEventArgs e) => OpenPath(_downloads.DownloadsPath);

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is DownloadItem item) OpenPath(item.Path);
    }

    private void RevealFile_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not DownloadItem item) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{item.Path}\"", UseShellExecute = true }); } catch { }
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    private async void SendChat_Click(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await SendChatAsync();
        }
    }

    private async Task SendChatAsync()
    {
        var prompt = ChatInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return;
        if (string.IsNullOrWhiteSpace(Settings.RouterAiKey))
        {
            RouterStatus.Text = "Укажите ключ RouterAI";
            SelectTab("settings");
            return;
        }
        var agent = AgentCombo.SelectedItem as AgentPreset ?? AgentPreset.Defaults[0];
        Chat.Add(new ChatMessage { Role = "user", Content = prompt });
        ChatInput.Clear();
        EmptyChatText.Visibility = Visibility.Collapsed;
        SendButton.IsEnabled = false;
        ChatInput.IsEnabled = false;
        AgentStatus.Text = $"{agent.Name} думает…";
        _chatCts?.Cancel();
        _chatCts = new CancellationTokenSource();
        try
        {
            var history = Chat.Take(Chat.Count - 1).ToArray();
            var answer = await _ai.SendAsync(Settings.RouterAiKey, Settings.RouterAiModel, agent, history, prompt, _chatCts.Token);
            Chat.Add(new ChatMessage { Role = "assistant", Content = answer });
            AgentStatus.Text = $"{agent.Name} · {Settings.RouterAiModel}";
        }
        catch (OperationCanceledException) { AgentStatus.Text = "Запрос отменён"; }
        catch (Exception ex) { AgentStatus.Text = ex.Message; }
        finally
        {
            SendButton.IsEnabled = true;
            ChatInput.IsEnabled = true;
            ChatScroll.UpdateLayout();
            ChatScroll.ScrollToEnd();
            ChatInput.Focus();
        }
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _chatCts?.Cancel();
        Chat.Clear();
        EmptyChatText.Visibility = Visibility.Visible;
        AgentStatus.Text = "Новый диалог";
        ChatInput.Focus();
    }

    private void Agent_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_currentTab == "ai" && AgentCombo.SelectedItem is AgentPreset agent) AgentStatus.Text = agent.Description;
    }

    private async void SaveRouterKey_Click(object sender, RoutedEventArgs e)
    {
        Settings.RouterAiKey = RouterKeyInput.Password.Trim();
        SaveSettings();
        if (string.IsNullOrWhiteSpace(Settings.RouterAiKey))
        {
            RouterStatus.Text = "Ключ не указан";
            return;
        }
        RouterStatus.Text = "Загрузка…";
        try
        {
            var models = await _ai.GetModelsAsync(Settings.RouterAiKey);
            Sync(Models, models);
            RouterStatus.Text = $"Подключено · {models.Count} моделей";
            if (!models.Contains(Settings.RouterAiModel) && models.Count > 0) Settings.RouterAiModel = models[0];
        }
        catch (Exception ex) { RouterStatus.Text = ex.Message; }
    }

    private void Credentials_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender == RouterKeyInput) RouterStatus.Text = "Сохраните изменённый ключ";
        if (sender == WeatherKeyInput) WeatherStatus.Text = "Сохраните изменённый ключ";
    }

    private void SaveWeatherKey_Click(object sender, RoutedEventArgs e)
    {
        Settings.WeatherKey = WeatherKeyInput.Password.Trim();
        SaveSettings();
        WeatherStatus.Text = string.IsNullOrWhiteSpace(Settings.WeatherKey) ? "Ключ не указан" : "Ключ сохранён";
        _weatherTimer.Stop();
        if (!string.IsNullOrWhiteSpace(Settings.WeatherKey)) _weatherTimer.Start();
    }

    private async void RefreshWeather_Click(object sender, RoutedEventArgs e) => await RefreshWeatherAsync(true);

    private async Task RefreshWeatherAsync(bool reportEmptyKey)
    {
        Settings.WeatherCity = WeatherCityInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(Settings.WeatherKey))
        {
            if (reportEmptyKey) Weather.Condition = "Добавьте ключ OpenWeatherMap в настройках";
            return;
        }
        if (string.IsNullOrWhiteSpace(Settings.WeatherCity)) return;
        _weatherCts?.Cancel();
        _weatherCts = new CancellationTokenSource();
        Weather.Updated = "Обновление…";
        try
        {
            var info = await _weather.GetCurrentAsync(Settings.WeatherKey, Settings.WeatherCity, Settings.WeatherLanguage, _weatherCts.Token);
            Weather.City = info.City;
            Weather.Temperature = info.Temperature;
            Weather.Condition = info.Condition;
            Weather.Details = info.Details;
            Weather.Icon = info.Icon;
            Weather.Updated = info.Updated;
            SaveSettings();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Weather.Updated = ex.Message; }
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_ready) return;
        Dispatcher.BeginInvoke(() =>
        {
            ApplyAppearance();
            _saveTimer.Stop();
            _saveTimer.Start();
            if (e.PropertyName == nameof(AppSettings.LaunchAtStartup)) ApplyStartup();
        });
    }

    private void ApplyAppearance()
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(Settings.Accent);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Application.Current.Resources["AccentBrush"] = brush;
        }
        catch { }
        IslandBorder.Opacity = Settings.Opacity;
        if (_expanded)
        {
            var monitor = GetTargetMonitor();
            var scale = GetMonitorScale(monitor.handle);
            Width = Math.Min(Settings.ExpandedWidth, (monitor.info.Work.Right - monitor.info.Work.Left) / scale - 20);
            Height = Math.Min(Settings.ExpandedHeight, (monitor.info.Work.Bottom - monitor.info.Work.Top) / scale - 12);
        }
        else if (!_toastVisible)
        {
            Width = Settings.IslandWidth;
            Height = Settings.IslandHeight;
            CompactPanel.Height = Settings.IslandHeight;
        }
        IslandBorder.CornerRadius = new CornerRadius(0, 0, Settings.CornerRadius, Settings.CornerRadius);
        PositionWindow();
    }

    private void Accent_Click(object sender, RoutedEventArgs e) => Settings.Accent = (string)((Button)sender).Tag;

    private void ScreenMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Settings.ScreenMode = ScreenModeCombo.SelectedIndex == 1 ? "primary" : "cursor";
        PositionWindow();
    }

    private void ApplyStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null) return;
            if (Settings.LaunchAtStartup && Environment.ProcessPath is { } path) key.SetValue("DynamicIsland", $"\"{path}\"");
            else key.DeleteValue("DynamicIsland", false);
        }
        catch (Exception ex) { SystemStatus.Text = "Автозапуск: " + ex.Message; }
    }

    private void SaveSettings()
    {
        try { _settingsService.Save(Settings); } catch { }
    }

    private void NotificationSettings_Click(object sender, RoutedEventArgs e) =>
        OpenPath("ms-settings:notifications");

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _closing = true;
        SaveSettings();
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        SaveSettings();
        _cursorTimer.Stop();
        _clockTimer.Stop();
        _mediaTimer.Stop();
        _weatherTimer.Stop();
        _weatherCts?.Cancel();
        _chatCts?.Cancel();
        if (_windowHandle != IntPtr.Zero) NativeMethods.RemoveClipboardFormatListener(_windowHandle);
        _notifications.Dispose();
        _media.Dispose();
        _system.Dispose();
        _downloads.Dispose();
    }

    private static void Sync<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }
}
