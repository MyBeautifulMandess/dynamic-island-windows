using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
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
    private readonly NotesService _notesService = new();
    private readonly CalendarService _calendarService = new();
    private readonly ReminderService _reminderService = new();
    private readonly QuickFilesService _quickFiles;
    private readonly DispatcherTimer _cursorTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private readonly DispatcherTimer _toastTimer = new();
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _brightnessTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private TrayIconService? _tray;
    private readonly List<FrameworkElement> _panels = [];
    private readonly Dictionary<string, FrameworkElement> _panelMap = [];
    private readonly Dictionary<FrameworkElement, TranslateTransform> _panelTransforms = [];
    private readonly ObservableCollection<CalendarDayItem> _pickerDays = [];
    private readonly ObservableCollection<TimeSlot> _timeSlots = [];
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
    private DateTime _expandedAt;
    private DateTime _fullScreenCheckedAt = DateTime.MinValue;
    private DateTime _lastTrimAt = DateTime.MinValue;
    private DispatcherTimer? _trimTimer;
    private static readonly TimeSpan MinTrimInterval = TimeSpan.FromSeconds(60);
    private bool _fullScreenActive;
    private string _currentTab = "home";
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selectedDay = DateTime.Today;
    private DateTime _pickerMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _reminderDate = DateTime.Today;
    private TimeSpan _reminderTime = new(9, 0, 0);
    private ReminderItem? _activeReminder;
    private NoteItem? _editingNote;
    private readonly List<ReminderItem> _allReminders = [];
    private const double PopupWidth = 418;
    private const double PopupHeight = 158;
    private const int TickMilliseconds = 160;

    public AppSettings Settings { get; }
    public Models.MediaState Media { get; } = new();
    public WeatherInfo Weather { get; } = new();
    public ObservableCollection<NotificationItem> Notifications { get; } = [];
    public ObservableCollection<ClipboardItem> ClipboardItems { get; } = [];
    public ObservableCollection<DownloadItem> Downloads { get; } = [];
    public ObservableCollection<ChatMessage> Chat { get; } = [];
    public ObservableCollection<AgentPreset> Agents { get; } = [];
    public ObservableCollection<string> Models { get; } = [];
    public ObservableCollection<NoteItem> Notes { get; } = [];
    public ObservableCollection<CalendarEventItem> CalendarEvents { get; } = [];
    public ObservableCollection<CalendarDayItem> CalendarDays { get; } = [];
    public ObservableCollection<CalendarEventItem> DayEvents { get; } = [];
    public ObservableCollection<ReminderItem> Reminders { get; } = [];
    public ObservableCollection<ReminderItem> DoneReminders { get; } = [];
    public ObservableCollection<DownloadItem> QuickFiles { get; } = [];
    public ObservableCollection<PanelOption> PanelOptions { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        Settings = _settingsService.Load();
        _quickFiles = new QuickFilesService(() => Settings.QuickFolder);
        DataContext = this;
        foreach (var agent in AgentPreset.Defaults) Agents.Add(agent);
        AgentCombo.SelectedIndex = 0;
        AiKeyInput.Password = Settings.AiKey;
        WeatherKeyInput.Password = Settings.WeatherKey;
        ScreenModeCombo.SelectedIndex = Settings.ScreenMode == "primary" ? 1 : 0;
        _panelMap.Add("home", HomePanel);
        _panelMap.Add("calendar", CalendarPanel);
        _panelMap.Add("reminders", RemindersPanel);
        _panelMap.Add("notes", NotesPanel);
        _panelMap.Add("quickfiles", QuickFilesPanel);
        _panelMap.Add("media", MediaPanel);
        _panelMap.Add("notifications", NotificationsPanel);
        _panelMap.Add("ai", AiPanel);
        _panelMap.Add("clipboard", ClipboardPanel);
        _panelMap.Add("files", FilesPanel);
        _panelMap.Add("weather", WeatherPanel);
        _panelMap.Add("settings", SettingsPanel);
        _panels.AddRange(_panelMap.Values);
        foreach (var note in _notesService.Load()) Notes.Add(note);
        foreach (var item in _calendarService.Load()) CalendarEvents.Add(item);
        _allReminders.AddRange(_reminderService.Load());
        RenderCalendar();
        RenderReminders();
        BuildPanelOptions();
        BuildTimeSlots();
        RenderPicker();
        UpdateReminderPickers();

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
        _quickFiles.Changed += items => Dispatcher.Invoke(() =>
        {
            Sync(QuickFiles, items);
            QuickFilesStatus.Text = _quickFiles.Folder;
        });
        _system.RadioChanged += () => Dispatcher.Invoke(UpdateRadioStates);
        Settings.PropertyChanged += Settings_PropertyChanged;

        _cursorTimer.Tick += Cursor_Tick;
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); if (!IsCursorInsideIsland()) Collapse(); };
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
        _reminderTimer.Tick += (_, _) => CheckReminders();

        SyncClipboard();
        Sync(Downloads, _downloads.Items);
        UpdateClock();
        UpdateWeatherPill();
        QuickFileStatusRefresh();
        ApplyAppearance();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        Diagnostics.Log("Window_Loaded: start");
        try
        {
            var source = HwndSource.FromHwnd(_windowHandle);
            source?.AddHook(WindowHook);
            NativeMethods.AddClipboardFormatListener(_windowHandle);
        }
        catch (Exception ex) { Diagnostics.Log("Window_Loaded: hook failed — " + ex.Message); }

        try
        {
            _tray = new TrayIconService(
                () => Dispatcher.Invoke(() => Expand("home")),
                () => Dispatcher.Invoke(() => Expand("settings")),
                () => Dispatcher.Invoke(ExitApp));
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Window_Loaded: tray icon failed — " + ex.Message);
            SystemStatus.Text = "Иконка в трее недоступна: " + ex.Message;
        }

        Collapse(false);
        _cursorTimer.Start();
        _clockTimer.Start();
        if (!string.IsNullOrWhiteSpace(Settings.WeatherKey)) _weatherTimer.Start();
        _ready = true;
        ApplyStartup();
        UpdateRadioStates();
        UpdateSystemControls();
        Diagnostics.Log($"Window_Loaded: hot corner={Settings.HotCornerEnabled}, fullscreen={NativeMethods.IsFullScreenActive()}");

        await SafeAsync("media", _media.InitializeAsync);
        await SafeAsync("system", _system.InitializeAsync);
        await SafeAsync("notifications", _notifications.InitializeAsync);

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
        _quickFiles.Refresh();
        _reminderTimer.Start();
        ReminderSnoozeCombo.SelectedIndex = SnoozeIndex(Settings.SnoozeMinutes);
        SnoozeCombo.SelectedIndex = SnoozeIndex(Settings.SnoozeMinutes);
        ReminderToneCombo.SelectedIndex = Settings.ReminderTone switch { "double" => 1, "quiet" => 2, _ => 0 };
        CheckReminders();
        if (!string.IsNullOrWhiteSpace(Settings.WeatherKey)) _ = RefreshWeatherAsync(false);
        Diagnostics.Log("Window_Loaded: ready");
    }

    private static async Task SafeAsync(string name, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Diagnostics.Log($"Initialize {name} failed — {ex.Message}"); }
    }

    private void BuildPanelOptions()
    {
        var visible = Settings.HiddenPanels.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var order = Settings.PanelOrder.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(id => _panelMap.ContainsKey(id)).Distinct().ToList();
        var catalog = PanelCatalog.All.Select(x => x.Id).ToList();
        for (var i = 0; i < catalog.Count; i++)
        {
            if (order.Contains(catalog[i])) continue;
            var previous = catalog.Take(i).LastOrDefault(order.Contains);
            order.Insert(previous is null ? 0 : order.IndexOf(previous) + 1, catalog[i]);
        }
        PanelOptions.Clear();
        foreach (var id in order)
            PanelOptions.Add(new PanelOption
            {
                Id = id,
                Title = PanelCatalog.Title(id),
                IsVisible = !visible.Contains(id)
            });
        BuildNavigation();
    }

    private void BuildNavigation()
    {
        NavListPanel.Children.Clear();
        foreach (var option in PanelOptions.Where(x => x.IsVisible).Append(new PanelOption { Id = "settings", Title = "Настройки", IsVisible = true }))
        {
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["IconButton"],
                Content = PanelCatalog.Glyph(option.Id),
                Width = 27,
                Height = 38,
                FontSize = 14,
                Tag = option.Id,
                ToolTip = option.Title,
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["IconFont"]
            };
            button.Click += Nav_Click;
            NavListPanel.Children.Add(button);
        }
    }

    private void UpdatePanelSettings()
    {
        var home = PanelOptions.FirstOrDefault(x => x.Id == "home");
        if (home is not null) home.IsVisible = true;
        Settings.PanelOrder = string.Join(',', PanelOptions.Select(x => x.Id));
        Settings.HiddenPanels = string.Join(',', PanelOptions.Where(x => !x.IsVisible).Select(x => x.Id));
        BuildNavigation();
        if (!PanelOptions.Any(x => x.IsVisible && x.Id == _currentTab) && _currentTab != "settings")
            SelectTab("home");
    }

    private void PanelToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdatePanelSettings();
    }

    private void PanelUp_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not PanelOption option) return;
        var index = PanelOptions.IndexOf(option);
        if (index > 0) PanelOptions.Move(index, index - 1);
        UpdatePanelSettings();
    }

    private void PanelDown_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not PanelOption option) return;
        var index = PanelOptions.IndexOf(option);
        if (index >= 0 && index < PanelOptions.Count - 1) PanelOptions.Move(index, index + 1);
        UpdatePanelSettings();
    }

    private void UpdateWeatherPill()
    {
        CompactWeatherPanel.Visibility = WeatherVisibleInPill ? Visibility.Visible : Visibility.Collapsed;
        CompactLabel.Visibility = WeatherVisibleInPill || Media.HasMedia ? Visibility.Collapsed : Visibility.Visible;
        if (WeatherVisibleInPill) CompactTrack.Text = "";
    }

    private void WeatherPill_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) UpdateWeatherPill();
    }

    private void QuickFileStatusRefresh()
    {
        QuickFilesStatus.Text = string.IsNullOrWhiteSpace(Settings.QuickFolder) ? "Папка не выбрана в настройках" : Settings.QuickFolder;
        _quickFiles.Refresh();
    }

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmClipboardUpdate) _clipboard.Capture();
        if (msg == NativeMethods.WmDpiChanged) Dispatcher.BeginInvoke(PositionWindow);
        return IntPtr.Zero;
    }

    private void Cursor_Tick(object? sender, EventArgs e)
    {
        if (_closing || !NativeMethods.GetCursorPos(out var point)) return;
        if (_toastVisible) return;
        if (_expanded)
        {
            if (_activeReminder is not null) return;
            if (!IsCursorInsideIsland() && (DateTime.UtcNow - _expandedAt).TotalMilliseconds > 700)
            {
                if (!_collapseTimer.IsEnabled) _collapseTimer.Start();
            }
            else _collapseTimer.Stop();
            return;
        }
        if (!Settings.HotCornerEnabled)
        {
            if (_peekVisible) Collapse();
            return;
        }
        var monitor = NativeMethods.GetMonitor(point);
        var workTop = monitor.Work.Top;
        if (_peekVisible)
        {
            if (IsFullScreenCached()) { Collapse(false); return; }
            if (!IsCursorInsideIsland() && point.Y > workTop + HotZone + 30) Collapse();
            return;
        }
        if (IsFullScreenCached())
        {
            if (point.Y <= workTop + HotZone) LogBlockedOnce("fullscreen");
            return;
        }
        if (point.Y >= monitor.Monitor.Top && point.Y <= workTop + HotZone && IsOnActiveScreen(point, monitor))
            ShowPeek();
    }

    private const int HotZone = 8;
    private string _lastBlockReason = "";

    private void LogBlockedOnce(string reason)
    {
        if (_lastBlockReason == reason) return;
        _lastBlockReason = reason;
        Diagnostics.Log("Hot corner suppressed: " + reason);
    }

    private static bool IsOnActiveScreen(NativeMethods.Point point, NativeMethods.MonitorInfo monitor) =>
        point.X >= monitor.Monitor.Left && point.X <= monitor.Monitor.Right;

    private bool IsFullScreenCached()
    {
        var now = DateTime.UtcNow;
        if ((now - _fullScreenCheckedAt).TotalMilliseconds < 400) return _fullScreenActive;
        _fullScreenCheckedAt = now;
        _fullScreenActive = NativeMethods.IsFullScreenActive();
        return _fullScreenActive;
    }

    private bool IsCursorInsideIsland()
    {
        if (!IsVisible) return false;
        if (!NativeMethods.GetCursorPos(out var point)) return IsMouseOver;
        if (_windowHandle == IntPtr.Zero || !NativeMethods.GetWindowRect(_windowHandle, out var rect)) return IsMouseOver;
        const int margin = 6;
        return point.X >= rect.Left - margin && point.X <= rect.Right + margin &&
               point.Y >= rect.Top - margin && point.Y <= rect.Bottom + margin;
    }

    private void ShowPeek()
    {
        if (_expanded || _toastVisible || _closing || _activeReminder is not null) return;
        _peekVisible = true;
        _collapseTimer.Stop();
        ExpandedPanel.Visibility = Visibility.Collapsed;
        ToastPanel.Visibility = Visibility.Collapsed;
        CompactPanel.Visibility = Visibility.Visible;
        SetWindowSize(Settings.IslandWidth, Settings.IslandHeight);
        PositionWindow();
        if (!IsVisible) Show();
        NativeMethods.AssertTopmost(_windowHandle);
        AnimateIn(190);
    }

    private void AnimateIn(int milliseconds)
    {
        StopIslandAnimation();
        IslandBorder.Opacity = Settings.Opacity;
        if (milliseconds <= 0) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var full = new Rect(0, 0, Width, Height);
        var start = new Rect(0, 0, Width, Math.Min(Height * 0.45, Math.Max(6, Height * 0.45)));
        IslandClip.Rect = full;
        IslandClip.BeginAnimation(RectangleGeometry.RectProperty,
            new RectAnimation(start, full, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        IslandBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, Settings.Opacity, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    private void StopIslandAnimation()
    {
        IslandClip.BeginAnimation(RectangleGeometry.RectProperty, null);
        IslandBorder.BeginAnimation(OpacityProperty, null);
        IslandClip.Rect = new Rect(0, 0, Width, Height);
    }

    private void AnimateOut(int milliseconds, Action done)
    {
        StopIslandAnimation();
        IslandBorder.Opacity = Settings.Opacity;
        if (milliseconds <= 0) { done(); return; }
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var full = new Rect(0, 0, Width, Height);
        var end = new Rect(0, 0, Width, Math.Max(6, Height * 0.5));
        IslandClip.Rect = full;
        IslandClip.BeginAnimation(RectangleGeometry.RectProperty,
            new RectAnimation(full, end, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        var fade = new DoubleAnimation(Settings.Opacity, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) =>
        {
            StopIslandAnimation();
            IslandBorder.Opacity = Settings.Opacity;
            done();
        };
        IslandBorder.BeginAnimation(OpacityProperty, fade);
    }

    private void AnimateSize(double targetWidth, double targetHeight, int milliseconds)
    {
        var previousWidth = ActualWidth > 1 ? ActualWidth : Width;
        var previousHeight = ActualHeight > 1 ? ActualHeight : Height;
        StopSizeAnimation();
        var monitor = GetTargetMonitor();
        var scale = GetMonitorScale(monitor.handle);
        var workLeft = monitor.info.Work.Left / scale;
        var workTop = monitor.info.Work.Top / scale;
        var workWidth = (monitor.info.Work.Right - monitor.info.Work.Left) / scale;
        var desiredLeft = workLeft + (workWidth - targetWidth) / 2 + Settings.HorizontalOffset;
        var targetLeft = Math.Clamp(desiredLeft, workLeft, Math.Max(workLeft, workLeft + workWidth - targetWidth));
        var targetTop = workTop;
        var full = new Rect(0, 0, targetWidth, targetHeight);
        Width = targetWidth;
        Height = targetHeight;
        Left = targetLeft;
        Top = targetTop;
        if (!IsVisible || milliseconds <= 0)
        {
            IslandClip.Rect = full;
            return;
        }

        var startWidth = Math.Clamp(previousWidth, 120, targetWidth);
        var startHeight = Math.Clamp(previousHeight, 28, targetHeight);
        var start = new Rect((targetWidth - startWidth) / 2, 0, startWidth, startHeight);
        IslandClip.Rect = full;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        IslandClip.BeginAnimation(RectangleGeometry.RectProperty,
            new RectAnimation(start, full, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        if (targetWidth > startWidth + 80 || targetHeight > startHeight + 40)
            ExpandedPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.35, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    private void StopSizeAnimation()
    {
        IslandClip.BeginAnimation(RectangleGeometry.RectProperty, null);
        ExpandedPanel.BeginAnimation(OpacityProperty, null);
        IslandClip.Rect = new Rect(0, 0, Width, Height);
    }

    private void SetWindowSize(double width, double height)
    {
        Width = width;
        Height = height;
        IslandClip.Rect = new Rect(0, 0, width, height);
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
        _expandedAt = DateTime.UtcNow;
        _peekVisible = false;
        _toastVisible = false;
        ToastPanel.Visibility = Visibility.Collapsed;
        ReminderPopup.Visibility = Visibility.Collapsed;
        CompactPanel.Visibility = Visibility.Collapsed;
        ExpandedPanel.Visibility = Visibility.Visible;
        var monitor = GetTargetMonitor();
        var scale = GetMonitorScale(monitor.handle);
        var availableWidth = (monitor.info.Work.Right - monitor.info.Work.Left) / scale - 20;
        var availableHeight = (monitor.info.Work.Bottom - monitor.info.Work.Top) / scale - 12;
        var targetWidth = Math.Min(Settings.ExpandedWidth, availableWidth);
        var targetHeight = Math.Min(Settings.ExpandedHeight, availableHeight);
        var wasVisible = IsVisible;
        SelectTab(tab);
        if (!wasVisible)
        {
            AnimateSize(targetWidth, targetHeight, 0);
            Show();
            NativeMethods.AssertTopmost(_windowHandle);
            AnimateIn(180);
        }
        else
        {
            AnimateSize(targetWidth, targetHeight, 240);
            NativeMethods.AssertTopmost(_windowHandle);
        }
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
        if (_activeReminder is not null)
        {
            ReminderPopup.Visibility = Visibility.Visible;
            AnimateSize(PopupWidth, PopupHeight, 180);
            if (!IsVisible) Show();
            return;
        }
        if (!IsVisible) { PositionWindow(); return; }
        if (!animate) { Hide(); PositionWindow(); TrimMemory(); return; }
        AnimateOut(150, () =>
        {
            // A reminder or a toast can arrive during the collapse animation and
            // re-show the window; hiding it here would leave the island invisible
            // with an active reminder, which never clears on its own.
            if (_activeReminder is not null || _expanded || _toastVisible || _peekVisible) return;
            Hide();
            SetWindowSize(Settings.IslandWidth, Settings.IslandHeight);
            PositionWindow();
            TrimMemory();
        });
    }

    /// <summary>
    /// Возвращает неиспользуемые страницы памяти системе. Вызывается только когда
    /// островок полностью скрыт, не чаще раза в минуту и в фоне, чтобы не влиять
    /// на анимацию.
    /// </summary>
    private void TrimMemory()
    {
        var now = DateTime.UtcNow;
        var wait = MinTrimInterval - (now - _lastTrimAt);
        if (wait > TimeSpan.Zero)
        {
            // Не отбрасываем запрос: иначе скрытие в пределах минуты после прошлой
            // очистки оставляло рабочий набор раздутым до следующего сворачивания.
            if (_trimTimer is not null) return;
            _trimTimer = new DispatcherTimer { Interval = wait };
            _trimTimer.Tick += (_, _) =>
            {
                _trimTimer?.Stop();
                _trimTimer = null;
                if (!IsVisible) TrimMemory();
            };
            _trimTimer.Start();
            return;
        }
        _lastTrimAt = now;
        Task.Run(() =>
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Optimized, false, true);
                NativeMethods.TrimWorkingSet();
            }
            catch { }
        });
    }

    private void PositionWindow()
    {
        if (_closing) return;
        var target = GetTargetMonitor();
        var scale = GetMonitorScale(target.handle);
        var workLeft = target.info.Work.Left / scale;
        var workTop = target.info.Work.Top / scale;
        var workWidth = (target.info.Work.Right - target.info.Work.Left) / scale;
        var left = workLeft + (workWidth - Width) / 2 + Settings.HorizontalOffset;
        var maxLeft = workLeft + workWidth - Width;
        Left = Math.Clamp(left, workLeft, Math.Max(workLeft, maxLeft));
        Top = workTop;
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

    private double GetMonitorScale(IntPtr monitor)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0) return dpi / 96d;
        }
        catch { }
        try
        {
            if (_windowHandle != IntPtr.Zero)
            {
                var windowDpi = NativeMethods.GetDpiForWindow(_windowHandle);
                if (windowDpi > 0) return windowDpi / 96d;
            }
        }
        catch { }
        return 1;
    }

    private void ShowToast(NotificationItem item)
    {
        if (_expanded || _closing || _activeReminder is not null) return;
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
        var wasVisible = IsVisible;
        PositionWindow();
        AnimateSize(440, 126, wasVisible ? 180 : 0);
        if (!IsVisible) Show();
        NativeMethods.AssertTopmost(_windowHandle);
        if (!wasVisible) AnimateIn(180);
        _toastTimer.Interval = TimeSpan.FromSeconds(Settings.NotificationSeconds);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void SelectTab(string tab)
    {
        if (_closing) return;
        if (!_panelMap.TryGetValue(tab, out var selected)) tab = "home";
        var changed = _currentTab != tab;
        _currentTab = tab;
        HeaderContext.Text = "  /  " + (tab == "settings" ? "Настройки" : PanelCatalog.Title(tab));
        foreach (var panel in _panels) panel.Visibility = Visibility.Collapsed;
        selected = _panelMap[tab];
        selected.Visibility = Visibility.Visible;
        if (changed) AnimatePanelIn(selected);
        if (tab != "ai" && SendButton.IsEnabled) AgentStatus.Text = "";
        if (tab == "media" && Media.IsPlaying) _mediaTimer.Start();
        else _mediaTimer.Stop();
        if (tab == "settings")
        {
            NotificationStatus.Text = _notifications.Status;
            SystemStatus.Text = string.IsNullOrWhiteSpace(_system.Status) ? "Системные интерфейсы готовы" : _system.Status;
            AiStatus.Text = string.IsNullOrWhiteSpace(Settings.AiKey) ? "Ключ не указан" : Settings.AiProvider;
        }
        if (tab == "weather" && Weather.TemperatureValue is null) _ = RefreshWeatherAsync(false);
        if (tab == "calendar")
        {
            _calendarMonth = new DateTime(_selectedDay.Year, _selectedDay.Month, 1);
            RenderCalendar();
        }
        if (tab == "reminders")
        {
            RenderReminders();
            RemindersStatus.Text = $"{Reminders.Count(x => !x.Done)} активных";
        }
        if (tab == "quickfiles") { QuickFilesStatus.Text = string.IsNullOrWhiteSpace(Settings.QuickFolder) ? "Папка не выбрана в настройках" : Settings.QuickFolder; _quickFiles.Refresh(); }
        if (tab == "notifications") UpdateNotifications(_notifications.Items);
    }

    /// <summary>
    /// Мягкое появление панели при переключении: прозрачность и короткий подъём.
    /// Трансформация создаётся один раз на панель, чтобы не мусорить в куче при
    /// каждом переходе.
    /// </summary>
    private void AnimatePanelIn(FrameworkElement panel)
    {
        if (!_panelTransforms.TryGetValue(panel, out var transform))
        {
            transform = new TranslateTransform();
            panel.RenderTransform = transform;
            _panelTransforms[panel] = transform;
        }
        panel.BeginAnimation(OpacityProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        panel.Opacity = 1;
        transform.Y = 0;
        if (!SystemParameters.ClientAreaAnimation) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(TickMilliseconds);
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
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
        CompactTrack.Text = snapshot.HasMedia && !WeatherVisibleInPill ? snapshot.Title : "";
        CompactLabel.Visibility = snapshot.HasMedia || WeatherVisibleInPill ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool WeatherVisibleInPill => Settings.ShowWeatherInPill && !string.IsNullOrWhiteSpace(Settings.WeatherKey);

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
        var empty = Notifications.Count == 0;
        NoNotificationsHome.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        NoNotificationsPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        HomeNotificationsList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        NotificationsList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ClearNotificationsPanelButton.IsEnabled = !empty;
    }

    private async void RefreshNotifications_Click(object sender, RoutedEventArgs e) => await _notifications.RefreshAsync(false);

    private void ClearNotifications_Click(object sender, RoutedEventArgs e)
    {
        _notifications.ClearAll();
        NotificationStatus.Text = "Список уведомлений очищен";
    }

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

    private void AddNote_Click(object sender, RoutedEventArgs e)
    {
        var text = NotesInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_editingNote is not null)
        {
            var index = Notes.IndexOf(_editingNote);
            var updated = _editingNote with { Text = text };
            if (index >= 0) Notes[index] = updated;
            else Notes.Insert(0, updated);
            _editingNote = null;
            EndNoteEditing();
            _notesService.Save(Notes);
            NotesStatus.Text = "Изменено";
            return;
        }
        Notes.Insert(0, new NoteItem(Guid.NewGuid().ToString("N"), text, DateTime.Now));
        NotesInput.Clear();
        _notesService.Save(Notes);
        NotesStatus.Text = "Сохранено";
    }

    private void EditNote_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not NoteItem note) return;
        _editingNote = note;
        NotesInput.Text = note.Text;
        NotesInput.CaretIndex = note.Text.Length;
        NotesInput.Focus();
        NoteSaveButton.Content = "Сохранить изменения";
        NoteCancelButton.Visibility = Visibility.Visible;
        NotesStatus.Text = "Редактирование";
    }

    private void CancelNoteEdit_Click(object sender, RoutedEventArgs e)
    {
        _editingNote = null;
        NotesInput.Clear();
        EndNoteEditing();
        NotesStatus.Text = "";
    }

    private void EndNoteEditing()
    {
        NoteSaveButton.Content = "Сохранить заметку";
        NoteCancelButton.Visibility = Visibility.Collapsed;
    }

    private void CopyNote_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not NoteItem note) return;
        try { Clipboard.SetText(note.Text); NotesStatus.Text = "Скопировано"; } catch { }
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not NoteItem note) return;
        if (ReferenceEquals(note, _editingNote))
        {
            _editingNote = null;
            NotesInput.Clear();
            EndNoteEditing();
        }
        Notes.Remove(note);
        _notesService.Save(Notes);
        NotesStatus.Text = "Удалено";
    }

    private void ChooseQuickFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Settings.QuickFolder, Title = "Папка для быстрых файлов" };
        if (dialog.ShowDialog(this) != true) return;
        Settings.QuickFolder = dialog.FolderName;
        QuickFileStatusRefresh();
    }

    private void RefreshQuickFiles_Click(object sender, RoutedEventArgs e) => _quickFiles.Refresh();

    private void OpenQuickFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Settings.QuickFolder)) { SelectTab("settings"); return; }
        OpenPath(Settings.QuickFolder);
    }

    private void ShowCalendar_Click(object sender, RoutedEventArgs e) => SelectTab("calendar");

    private void RenderCalendar()
    {
        CalendarMonthText.Text = _calendarMonth.ToString("MMMM yyyy");
        var offset = ((int)_calendarMonth.DayOfWeek + 6) % 7;
        var start = _calendarMonth.AddDays(-offset);
        var byDay = new Dictionary<DateTime, List<CalendarEventItem>>();
        foreach (var item in CalendarEvents)
        {
            var day = item.Start.Date;
            if (!byDay.TryGetValue(day, out var list)) byDay[day] = list = [];
            list.Add(item);
        }
        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        var todayBrush = new SolidColorBrush(Color.FromArgb(60, 0x8B, 0x7C, 0xFF));
        todayBrush.Freeze();
        CalendarDays.Clear();
        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            byDay.TryGetValue(date, out var items);
            CalendarDays.Add(new CalendarDayItem
            {
                Date = date,
                HasItems = items is { Count: > 0 },
                Opacity = date.Month == _calendarMonth.Month ? 1 : 0.32,
                Background = date == _selectedDay ? accent : date == DateTime.Today ? todayBrush : Brushes.Transparent,
                Foreground = date == _selectedDay ? Brushes.White : date == DateTime.Today ? accent : Brushes.White,
                ToolTip = items is { Count: > 0 }
                    ? string.Join("\n", items.OrderBy(x => x.Start).Select(x => $"{x.Start:HH:mm}  {x.Title}"))
                    : date.ToString("dd MMMM yyyy")
            });
        }
        CalendarSelectedText.Text = _selectedDay.ToString("dddd, d MMMM");
        CalendarGrid.ItemsSource = CalendarDays;
        var dayEvents = CalendarEvents.Where(x => x.Start.Date == _selectedDay).OrderBy(x => x.Start).ToList();
        Sync(DayEvents, dayEvents);
        CalendarStatus.Text = dayEvents.Count == 0 ? "На этот день событий нет" : $"{dayEvents.Count} на этот день";
        UpdateHomeCalendar();
    }

    private void CalendarPrev_Click(object sender, RoutedEventArgs e)
    {
        _calendarMonth = _calendarMonth.AddMonths(-1);
        RenderCalendar();
    }

    private void CalendarNext_Click(object sender, RoutedEventArgs e)
    {
        _calendarMonth = _calendarMonth.AddMonths(1);
        RenderCalendar();
    }

    private void CalendarToday_Click(object sender, RoutedEventArgs e)
    {
        _selectedDay = DateTime.Today;
        _calendarMonth = new DateTime(_selectedDay.Year, _selectedDay.Month, 1);
        RenderCalendar();
    }

    private void CalendarDay_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not DateTime date) return;
        _selectedDay = date.Date;
        if (_selectedDay.Month != _calendarMonth.Month || _selectedDay.Year != _calendarMonth.Year)
            _calendarMonth = new DateTime(_selectedDay.Year, _selectedDay.Month, 1);
        RenderCalendar();
    }

    private void AddCalendarEvent_Click(object sender, RoutedEventArgs e)
    {
        var title = CalendarTitleInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(title)) { CalendarStatus.Text = "Введите название события"; return; }
        var allDay = CalendarAllDayCheck.IsChecked == true;
        var start = _selectedDay.Date;
        if (!allDay)
        {
            if (!TryParseTime(CalendarTimeInput.Text, out var time))
            {
                CalendarStatus.Text = "Время в формате ЧЧ:ММ";
                return;
            }
            start = start.Add(time);
        }
        var minutes = CalendarReminderCheck.IsChecked == true ? 10 : 0;
        var item = new CalendarEventItem
        {
            Title = title,
            Start = start,
            End = allDay ? start.AddDays(1) : start.AddHours(1),
            IsAllDay = allDay,
            ReminderMinutes = minutes
        };
        if (minutes > 0)
        {
            var reminder = new ReminderItem
            {
                Title = title,
                Due = start.AddMinutes(-minutes),
                Source = "calendar",
                EventId = item.Id
            };
            if (reminder.Due <= DateTime.Now) reminder.Due = DateTime.Now.AddMinutes(1);
            item.ReminderId = reminder.Id;
            AddReminder(reminder);
        }
        CalendarEvents.Add(item);
        CalendarTitleInput.Clear();
        _calendarService.Save(CalendarEvents);
        RenderCalendar();
        CalendarStatus.Text = $"«{title}» добавлено";
    }

    private void DeleteCalendarEvent_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not CalendarEventItem item) return;
        CalendarEvents.Remove(item);
        if (!string.IsNullOrEmpty(item.ReminderId))
        {
            _allReminders.RemoveAll(x => x.Id == item.ReminderId);
            if (_activeReminder?.Id == item.ReminderId) HideReminderPopup();
            _reminderService.Save(_allReminders);
            RenderReminders();
        }
        _calendarService.Save(CalendarEvents);
        RenderCalendar();
    }

    private void CopyCalendarEvent_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not CalendarEventItem item) return;
        try
        {
            Clipboard.SetText($"{item.Title} — {item.Start:dd.MM.yyyy HH:mm}");
            CalendarStatus.Text = "Скопировано";
        }
        catch { }
    }

    private void UpdateHomeCalendar()
    {
        var next = CalendarEvents.Where(x => x.End >= DateTime.Now).OrderBy(x => x.Start).FirstOrDefault();
        HomeCalendarText.Text = next is null
            ? "Событий нет — добавьте в календаре"
            : $"{next.Title}\n{next.WhenText}  {next.EndText}";
    }

    private void RenderReminders()
    {
        Sync(Reminders, _allReminders.Where(x => !x.Done).OrderBy(x => x.Due));
        Sync(DoneReminders, _allReminders.Where(x => x.Done).OrderByDescending(x => x.CompletedAt ?? x.Due).Take(20));
        var hasActive = Reminders.Count > 0;
        NoRemindersText.Visibility = hasActive ? Visibility.Collapsed : Visibility.Visible;
        RemindersList.Visibility = hasActive ? Visibility.Visible : Visibility.Collapsed;
        DoneRemindersSection.Visibility = DoneReminders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (DoneReminders.Count == 0)
        {
            DoneRemindersList.Visibility = Visibility.Collapsed;
            DoneRemindersArrow.RenderTransform = new RotateTransform(0);
            _doneRemindersOpen = false;
        }
        RemindersStatus.Text = $"{Reminders.Count} активных";
    }

    private bool _doneRemindersOpen;

    private void ToggleDoneReminders_Click(object sender, RoutedEventArgs e)
    {
        _doneRemindersOpen = !_doneRemindersOpen;
        DoneRemindersList.Visibility = _doneRemindersOpen ? Visibility.Visible : Visibility.Collapsed;
        DoneRemindersArrow.RenderTransform = new RotateTransform(_doneRemindersOpen ? 90 : 0);
    }

    private void AddReminder(ReminderItem reminder, bool createEvent = true)
    {
        _allReminders.Add(reminder);
        if (reminder.Source != "calendar" && createEvent)
        {
            var item = new CalendarEventItem
            {
                Title = reminder.Title,
                Start = reminder.Due,
                End = reminder.Due.AddHours(1),
                ReminderId = reminder.Id
            };
            reminder.EventId = item.Id;
            CalendarEvents.Add(item);
            _calendarService.Save(CalendarEvents);
            if (_currentTab == "calendar") RenderCalendar();
            else UpdateHomeCalendar();
        }
        _reminderService.Save(_allReminders);
        RenderReminders();
        CheckReminders();
    }

    private void AddReminder_Click(object sender, RoutedEventArgs e)
    {
        var title = ReminderTitleInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(title)) { ReminderHint.Text = "Введите текст напоминания"; return; }
        var due = _reminderDate.Date.Add(_reminderTime);
        if (due <= DateTime.Now)
        {
            due = DateTime.Now.AddMinutes(1);
            _reminderDate = due.Date;
            _reminderTime = due.TimeOfDay;
            UpdateReminderPickers();
        }
        var createEvent = ReminderCalendarCheck.IsChecked == true;
        AddReminder(new ReminderItem { Title = title, Due = due }, createEvent);
        ReminderTitleInput.Clear();
        ReminderHint.Text = createEvent
            ? $"Добавлено на {due:dd.MM HH:mm} · событие в календаре"
            : $"Добавлено на {due:dd.MM HH:mm} · только напоминание";
    }

    private void BuildTimeSlots()
    {
        _timeSlots.Clear();
        for (var minutes = 0; minutes < 24 * 60; minutes += 30)
            _timeSlots.Add(new TimeSlot(TimeSpan.FromMinutes(minutes)));
        MarkTimeSlot(_reminderTime);
        TimeSlotList.ItemsSource = _timeSlots;
    }

    private void MarkTimeSlot(TimeSpan time)
    {
        foreach (var slot in _timeSlots) slot.IsSelected = slot.Time == time;
    }

    private void UpdateReminderPickers()
    {
        ReminderDateText.Text = FormatReminderDate(_reminderDate);
        ReminderTimeText.Text = $"{_reminderTime:hh\\:mm}";
        MarkTimeSlot(_reminderTime);
        if (_pickerMonth.Year != _reminderDate.Year || _pickerMonth.Month != _reminderDate.Month)
            _pickerMonth = new DateTime(_reminderDate.Year, _reminderDate.Month, 1);
        RenderPicker();
    }

    private static string FormatReminderDate(DateTime date) => date.Date switch
    {
        var d when d == DateTime.Today => "Сегодня",
        var d when d == DateTime.Today.AddDays(1) => "Завтра",
        var d when d == DateTime.Today.AddDays(-1) => "Вчера",
        _ => date.ToString("dd.MM.yyyy")
    };

    private void RenderPicker()
    {
        PickerMonthText.Text = _pickerMonth.ToString("MMMM yyyy");
        var offset = ((int)_pickerMonth.DayOfWeek + 6) % 7;
        var start = _pickerMonth.AddDays(-offset);
        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        var todayBrush = new SolidColorBrush(Color.FromArgb(48, 0xFF, 0xFF, 0xFF));
        todayBrush.Freeze();
        _pickerDays.Clear();
        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            _pickerDays.Add(new CalendarDayItem
            {
                Date = date,
                HasItems = false,
                Opacity = date.Month == _pickerMonth.Month ? 1 : 0.3,
                Background = date == _reminderDate.Date ? accent : date == DateTime.Today ? todayBrush : Brushes.Transparent,
                Foreground = date == _reminderDate.Date ? Brushes.White : Brushes.White,
                ToolTip = date.ToString("dd MMMM yyyy")
            });
        }
        PickerGrid.ItemsSource = _pickerDays;
    }

    private void ReminderDateButton_Click(object sender, RoutedEventArgs e)
    {
        _pickerMonth = new DateTime(_reminderDate.Year, _reminderDate.Month, 1);
        RenderPicker();
        ReminderTimePopup.IsOpen = false;
        ReminderDatePopup.IsOpen = true;
    }

    private void ReminderTimeButton_Click(object sender, RoutedEventArgs e)
    {
        MarkTimeSlot(_reminderTime);
        ReminderDatePopup.IsOpen = false;
        ReminderTimePopup.IsOpen = true;
    }

    private void PickerPrev_Click(object sender, RoutedEventArgs e)
    {
        _pickerMonth = _pickerMonth.AddMonths(-1);
        RenderPicker();
    }

    private void PickerNext_Click(object sender, RoutedEventArgs e)
    {
        _pickerMonth = _pickerMonth.AddMonths(1);
        RenderPicker();
    }

    private void PickerDay_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not DateTime date) return;
        _reminderDate = date.Date;
        UpdateReminderPickers();
        ReminderDatePopup.IsOpen = false;
    }

    private void PickerToday_Click(object sender, RoutedEventArgs e)
    {
        _reminderDate = DateTime.Today;
        UpdateReminderPickers();
        ReminderDatePopup.IsOpen = false;
    }

    private void PickerTomorrow_Click(object sender, RoutedEventArgs e)
    {
        _reminderDate = DateTime.Today.AddDays(1);
        UpdateReminderPickers();
        ReminderDatePopup.IsOpen = false;
    }

    private void PickerTime_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not TimeSlot slot) return;
        _reminderTime = slot.Time;
        UpdateReminderPickers();
        ReminderTimePopup.IsOpen = false;
    }

    private void CheckReminders()
    {
        // A reminder popup can only be dismissed while the island is on screen. If
        // the collapse animation hid the window after the popup was shown, the
        // popup would block the island forever: hot corner, toasts and the panel
        // all wait for the active reminder to clear. Drop the stale state so the
        // reminder is shown again instead of freezing the window.
        if (_activeReminder is not null && !IsVisible)
        {
            _activeReminder = null;
            ReminderPopup.Visibility = Visibility.Collapsed;
        }
        if (_activeReminder is not null) return;
        var due = _allReminders.Where(x => !x.Done && x.Due <= DateTime.Now).OrderBy(x => x.Due).FirstOrDefault();
        if (due is null) return;
        ShowReminderPopup(due);
    }

    private void ShowReminderPopup(ReminderItem reminder)
    {
        _activeReminder = reminder;
        ReminderPopupTitle.Text = reminder.Title;
        ReminderPopupTime.Text = reminder.WhenText;
        _expanded = false;
        _peekVisible = false;
        _toastVisible = false;
        ExpandedPanel.Visibility = Visibility.Collapsed;
        ToastPanel.Visibility = Visibility.Collapsed;
        CompactPanel.Visibility = Visibility.Collapsed;
        ReminderPopup.Visibility = Visibility.Visible;
        var wasVisible = IsVisible;
        PositionWindow();
        AnimateSize(PopupWidth, PopupHeight, wasVisible ? 180 : 0);
        if (!IsVisible) Show();
        NativeMethods.AssertTopmost(_windowHandle);
        if (!wasVisible) AnimateIn(180);
        PlayReminderSound();
        RenderReminders();
    }

    private void HideReminderPopup()
    {
        _activeReminder = null;
        ReminderPopup.Visibility = Visibility.Collapsed;
        Collapse();
        CheckReminders();
    }

    private void PlayReminderSound()
    {
        if (!Settings.ReminderSound) return;
        try
        {
            switch (Settings.ReminderTone)
            {
                case "double":
                    SystemSounds.Exclamation.Play();
                    break;
                case "quiet":
                    SystemSounds.Asterisk.Play();
                    break;
                default:
                    SystemSounds.Beep.Play();
                    break;
            }
        }
        catch { }
    }

    private void ReminderPopupDone_Click(object sender, RoutedEventArgs e)
    {
        if (_activeReminder is null) return;
        CompleteReminder(_activeReminder);
        HideReminderPopup();
        CheckReminders();
    }

    private void ReminderPopupSnooze_Click(object sender, RoutedEventArgs e)
    {
        if (_activeReminder is null) return;
        SnoozeReminder(_activeReminder, SelectedTag(ReminderSnoozeCombo, 10));
        HideReminderPopup();
        CheckReminders();
    }

    private void CompleteReminder(ReminderItem reminder)
    {
        reminder.Done = true;
        reminder.CompletedAt = DateTime.Now;
        _reminderService.Save(_allReminders);
        RenderReminders();
    }

    private void SnoozeReminder(ReminderItem reminder, int minutes)
    {
        var delta = DateTime.Now.AddMinutes(Math.Clamp(minutes, 1, 240)) - reminder.Due;
        reminder.Due = reminder.Due.Add(delta);
        if (!string.IsNullOrEmpty(reminder.EventId))
        {
            var item = CalendarEvents.FirstOrDefault(x => x.Id == reminder.EventId);
            if (item is not null)
            {
                var newStart = item.Start.Add(delta);
                item.End = newStart.Add(item.End - item.Start);
                item.Start = newStart;
                _calendarService.Save(CalendarEvents);
                RenderCalendar();
            }
        }
        _reminderService.Save(_allReminders);
        RenderReminders();
    }

    private void CompleteReminder_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not ReminderItem reminder) return;
        CompleteReminder(reminder);
        if (ReferenceEquals(reminder, _activeReminder)) HideReminderPopup();
    }

    private void SnoozeReminder_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not ReminderItem reminder) return;
        SnoozeReminder(reminder, Settings.SnoozeMinutes);
        if (ReferenceEquals(reminder, _activeReminder)) HideReminderPopup();
    }

    private void DeleteReminder_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not ReminderItem reminder) return;
        _allReminders.Remove(reminder);
        if (ReferenceEquals(reminder, _activeReminder)) HideReminderPopup();
        if (!string.IsNullOrEmpty(reminder.EventId))
        {
            var item = CalendarEvents.FirstOrDefault(x => x.Id == reminder.EventId);
            if (item is not null)
            {
                CalendarEvents.Remove(item);
                _calendarService.Save(CalendarEvents);
                RenderCalendar();
            }
        }
        _reminderService.Save(_allReminders);
        RenderReminders();
    }

    private void Snooze_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Settings.SnoozeMinutes = SelectedTag(SnoozeCombo, 10);
    }

    private void ReminderTone_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Settings.ReminderTone = ReminderToneCombo.SelectedIndex switch { 1 => "double", 2 => "quiet", _ => "soft" };
    }

    private static bool TryParseDate(string text, out DateTime date)
    {
        var value = text.Trim();
        if (DateTime.TryParseExact(value, ["dd.MM.yyyy", "d.M.yyyy", "dd.MM", "d.M"], null,
                System.Globalization.DateTimeStyles.None, out date))
        {
            if (date.Year < 2000) date = new DateTime(DateTime.Today.Year, date.Month, date.Day);
            return true;
        }
        if (DateTime.TryParse(value, out date)) return true;
        date = DateTime.Today;
        return false;
    }

    private static bool TryParseTime(string text, out TimeSpan time)
    {
        var value = text.Trim().Replace('.', ':');
        if (TimeSpan.TryParseExact(value, @"h\:mm", null, out time)) return true;
        if (TimeSpan.TryParse(value, out time)) return true;
        time = default;
        return false;
    }

    private static int SelectedTag(ComboBox combo, int fallback) =>
        combo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var value) ? value : fallback;

    private static int SnoozeIndex(int minutes) => minutes switch
    {
        <= 5 => 0,
        <= 10 => 1,
        <= 15 => 2,
        <= 30 => 3,
        _ => 4
    };

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
        if (string.IsNullOrWhiteSpace(Settings.AiKey))
        {
            AiStatus.Text = "Укажите ключ API";
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
            var answer = await _ai.SendAsync(Settings.AiBaseUrl, Settings.AiKey, Settings.AiModel, agent, history, prompt, _chatCts.Token);
            Chat.Add(new ChatMessage { Role = "assistant", Content = answer });
            // Диалог живёт только в памяти: держим последние 200 сообщений,
            // чтобы длинная переписка не росла бесконечно.
            while (Chat.Count > 200) Chat.RemoveAt(0);
            AgentStatus.Text = $"{agent.Name} · {Settings.AiModel}";
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

    private async void SaveAi_Click(object sender, RoutedEventArgs e)
    {
        Settings.AiBaseUrl = AiBaseUrlInput.Text.Trim();
        Settings.AiProvider = AiProviderInput.Text.Trim();
        Settings.AiModel = AiModelInput.Text.Trim();
        Settings.AiKey = AiKeyInput.Password.Trim();
        SaveSettings();
        if (string.IsNullOrWhiteSpace(Settings.AiKey))
        {
            AiStatus.Text = "Ключ не указан";
            return;
        }
        AiStatus.Text = "Загрузка…";
        try
        {
            var models = await _ai.GetModelsAsync(Settings.AiBaseUrl, Settings.AiKey);
            Sync(Models, models);
            AiStatus.Text = $"Подключено · {models.Count} моделей";
            if (!models.Contains(Settings.AiModel) && models.Count > 0) Settings.AiModel = models[0];
        }
        catch (Exception ex) { AiStatus.Text = ex.Message; }
    }

    private void Credentials_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender == AiKeyInput) AiStatus.Text = "Сохраните изменённый ключ";
        if (sender == WeatherKeyInput) WeatherStatus.Text = "Сохраните изменённый ключ";
    }

    private void SaveWeatherKey_Click(object sender, RoutedEventArgs e)
    {
        Settings.WeatherKey = WeatherKeyInput.Password.Trim();
        SaveSettings();
        WeatherStatus.Text = string.IsNullOrWhiteSpace(Settings.WeatherKey) ? "Ключ не указан" : "Ключ сохранён";
        _weatherTimer.Stop();
        if (!string.IsNullOrWhiteSpace(Settings.WeatherKey)) _weatherTimer.Start();
        UpdateWeatherPill();
        if (!string.IsNullOrWhiteSpace(Settings.WeatherKey)) _ = RefreshWeatherAsync(false);
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
        var token = _weatherCts.Token;
        Weather.Updated = "Обновление…";
        try
        {
            var info = await _weather.GetCurrentAsync(Settings.WeatherKey, Settings.WeatherCity, Settings.WeatherLanguage, token);
            if (token.IsCancellationRequested) return;
            var previous = Weather.Temperature;
            Weather.City = info.City;
            Weather.TemperatureValue = info.Temperature;
            Weather.Condition = info.Condition;
            Weather.Details = info.Details;
            Weather.Icon = info.Icon;
            Weather.FetchedAt = DateTime.Now;
            Weather.Updated = "Обновлено " + DateTime.Now.ToString("HH:mm");
            Diagnostics.Log($"Weather: {previous} → {Weather.Temperature} ({info.City})");
            UpdateWeatherPill();
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
            if (e.PropertyName == nameof(AppSettings.QuickFolder))
            {
                QuickFilesStatus.Text = string.IsNullOrWhiteSpace(Settings.QuickFolder) ? "Папка не выбрана в настройках" : Settings.QuickFolder;
                _quickFiles.ReloadFolder();
            }
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
        IslandBorder.CornerRadius = new CornerRadius(0, 0, Settings.CornerRadius, Settings.CornerRadius);
        IslandClip.Rect = new Rect(0, 0, Width, Height);
        if (_activeReminder is not null)
        {
            AnimateSize(PopupWidth, PopupHeight, 0);
            return;
        }
        if (_expanded)
        {
            var monitor = GetTargetMonitor();
            var scale = GetMonitorScale(monitor.handle);
            AnimateSize(
                Math.Min(Settings.ExpandedWidth, (monitor.info.Work.Right - monitor.info.Work.Left) / scale - 20),
                Math.Min(Settings.ExpandedHeight, (monitor.info.Work.Bottom - monitor.info.Work.Top) / scale - 12), 0);
            return;
        }
        if (_toastVisible)
        {
            AnimateSize(440, 126, 0);
            return;
        }
        AnimateSize(Settings.IslandWidth, Settings.IslandHeight, 0);
        if (IsVisible) AnimateIn(0);
    }

    private void Accent_Click(object sender, RoutedEventArgs e) => Settings.Accent = (string)((Button)sender).Tag;

    private void ScreenMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Settings.ScreenMode = ScreenModeCombo.SelectedIndex == 1 ? "primary" : "cursor";
        PositionWindow();
    }

    private static string? ResolveStartupTarget()
    {
        // Prefer the installed copy so that a development build launched from
        // the repository cannot hijack autostart for the released install.
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "DynamicIsland", "DynamicIsland.exe");
        if (File.Exists(installed)) return installed;
        return Environment.ProcessPath;
    }

    private void ApplyStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null) return;
            if (Settings.LaunchAtStartup && ResolveStartupTarget() is { } path)
            {
                var target = $"\"{path}\"";
                // Re-assert the current executable so a moved or rebuilt install
                // never leaves autostart pointing at a stale build.
                if (key.GetValue("DynamicIsland") as string != target)
                    key.SetValue("DynamicIsland", target);
            }
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

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApp();

    private void ExitApp()
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
        _quickFiles.Dispose();
        _reminderTimer.Stop();
        _tray?.Dispose();
        _tray = null;
    }

    /// <summary>
    /// Копирует последовательность в коллекцию, не пересобирая элементы интерфейса,
    /// если состав и порядок не изменились. Без этой проверки каждый опрос
    /// уведомлений пересоздавал все карточки в списках.
    /// </summary>
    private static void Sync<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        var items = source as IReadOnlyList<T> ?? source.ToList();
        if (ReferenceEquals(items, target)) return;
        if (target.Count == items.Count)
        {
            var same = true;
            for (var i = 0; i < items.Count; i++)
            {
                if (EqualityComparer<T>.Default.Equals(target[i], items[i])) continue;
                same = false;
                break;
            }
            if (same) return;
        }
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
