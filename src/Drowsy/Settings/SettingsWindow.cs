using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Drowsy.Interop;
using Windows.Graphics;
using static Drowsy.Settings.Ui;

namespace Drowsy.Settings;

/// <summary>
/// Status + settings window, built in code (no XAML) so it stays trimming/AOT friendly.
/// A sidebar with Status, Settings and Advanced pages, plus sleep history and the log.
/// Changes save automatically; the engine picks them up from config.json.
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    private const double MinDb = -90, MaxDb = -10;

    private readonly Config _config;
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _saveTimer;
    private bool _loading;
    private int _ticks;
    private AudioMonitorWindow? _audioMonitor;

    // Status
    private readonly Ellipse _stateDot = new() { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new();
    private readonly TextBlock _headline = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _reasons = new() { Spacing = 6 };
    private readonly ProgressBar _progress = new() { Maximum = 100 };
    private readonly TextBlock _lastActivity = new() { TextWrapping = TextWrapping.Wrap };
    private readonly InfoBar _notRunning = new()
    {
        Severity = InfoBarSeverity.Warning,
        Title = "Drowsy isn't running",
        Message = "Nothing will put the PC to sleep until it's started.",
        IsClosable = false,
    };

    // Settings
    private readonly ToggleSwitch _enabled = NewToggle();
    private readonly NumberBox _idleMinutes = NewNumberBox(1, 24 * 60, 1, 5);
    private readonly ToggleSwitch _startup = NewToggle();
    private readonly InfoBar _startupProblem = new()
    {
        Severity = InfoBarSeverity.Warning,
        Title = "Can't start with Windows from here",
        IsClosable = true,
    };
    private readonly ToggleSwitch _audio = NewToggle();
    private readonly ToggleSwitch _microphone = NewToggle();
    private readonly ToggleSwitch _controller = NewToggle();
    private readonly ToggleSwitch _display = NewToggle();
    private readonly NumberBox _warningSeconds = NewNumberBox(0, 600, 5, 30);
    private readonly NumberBox _thresholdDb = NewNumberBox(MinDb, MaxDb, 1, 5);
    private readonly PickerList _processes = new(
        "Also stay awake while these programs are running", "No programs added.", "Add program", Catalog.RunningApps);
    private readonly PickerList _ignoredAudioApps = new(
        "Except these apps (audio and microphone)", "None.", "Add app", Catalog.AudioApps);
    private readonly ToggleSwitch _ignoreInjected = NewToggle();
    private readonly PickerList _ignoredInputDevices = new(
        "Ignore input from these devices", "None.", "Add device", Catalog.InputDevices);

    // Pages
    private readonly NavigationView _nav = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsSettingsVisible = false,
        IsPaneToggleButtonVisible = false,
        OpenPaneLength = 200,
    };
    private readonly Border _pageHost = new();
    private readonly Dictionary<NavigationViewItem, UIElement> _pages = [];
    private readonly HistoryPage _historyPage = new();
    private readonly LogPage _logPage = new();
    private readonly ToggleSwitch _checkUpdates = NewToggle();
    private UpdatesPage? _updatesPage;
    private NavigationViewItem? _historyItem, _logItem, _updatesItem;
    private readonly StackPanel _lastSleep = new();
    private readonly StackPanel _recentActivity = new() { Spacing = 4 };

    public SettingsWindow()
    {
        _config = new ConfigStore().Current;

        Title = "Drowsy";
        SystemBackdrop = new MicaBackdrop();
        Ui.SetAppIcon(AppWindow);
        SizeAndCenter(820, 720);

        Content = BuildLayout();
        LoadSettings();
        HookAutoSave();

        DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
        _saveTimer = queue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(500);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Save();

        _refreshTimer = queue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(1);
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
        Refresh();

        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _audioMonitor?.Close();
            if (_saveTimer.IsRunning) { _saveTimer.Stop(); Save(); }
            Application.Current.Exit();
        };
    }

    // ---- Layout ------------------------------------------------------------------------------

    private UIElement BuildLayout()
    {
        AddPage("Status", "", BuildStatusPage());          // home
        AddPage("Settings", "", BuildSettingsPage());      // gear
        AddPage("Advanced", "", BuildAdvancedPage());      // wrench
        _nav.MenuItems.Add(new NavigationViewItemSeparator());
        _historyItem = AddPage("Sleep history", "", _historyPage.View);
        _logItem = AddPage("Log", "", _logPage.View);
        _updatesPage = new UpdatesPage(_checkUpdates, Close);
        _updatesItem = AddPage("Updates", "\uE895", _updatesPage.View, footer: true); // sync arrows

        _nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem item && _pages.TryGetValue(item, out UIElement? page))
            {
                _pageHost.Child = page;
                if (item == _logItem) _logPage.ReloadIfChanged();
                if (item == _updatesItem) _updatesPage?.OnShown();
            }
        };
        _nav.SelectedItem = _nav.MenuItems[0];

        // The "not running" warning sits above every page.
        var start = new Button { Content = "Start" };
        start.Click += (_, _) => Program.LaunchEngine();
        _notRunning.ActionButton = start;
        _notRunning.Margin = new Thickness(28, 12, 28, 0);
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_pageHost, 1);
        content.Children.Add(_notRunning);
        content.Children.Add(_pageHost);
        _nav.Content = content;
        return _nav;
    }

    private NavigationViewItem AddPage(string title, string glyph, UIElement page, bool footer = false)
    {
        var item = new NavigationViewItem { Content = title, Icon = new FontIcon { Glyph = glyph } };
        (footer ? _nav.FooterMenuItems : _nav.MenuItems).Add(item);
        _pages[item] = page;
        return item;
    }

    /// <summary>Opens the Updates page (Drowsy.exe --settings --updates, from the tray).</summary>
    internal void ShowUpdates() => Navigate(_updatesItem);

    private void Navigate(NavigationViewItem? item)
    {
        if (item is not null) _nav.SelectedItem = item;
    }

    private UIElement BuildStatusPage()
    {
        // Windows type ramp: Subtitle = 20 semibold.
        _title.FontSize = 20;
        _title.FontWeight = FontWeights.SemiBold;
        _headline.Foreground = Brush("TextFillColorSecondaryBrush", Colors.Gray);
        _lastActivity.FontSize = 12;
        _lastActivity.Foreground = Brush("TextFillColorTertiaryBrush", Colors.Gray);
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleRow.Children.Add(_stateDot);
        titleRow.Children.Add(_title);

        return Page("Status",
            Card(spacing: 8, titleRow, _headline, _reasons, _progress, _lastActivity),
            Header("Last sleep"),
            Card(spacing: 0, _lastSleep),
            Link("See all sleep history", () => Navigate(_historyItem)),
            Header("Recent activity"),
            Card(spacing: 0, Padded(_recentActivity)),
            Link("View the full log", () => Navigate(_logItem)));
    }

    private UIElement BuildSettingsPage() => Page("Settings",
        Header("Sleep"),
        Card(spacing: 0,
            Row("Put the PC to sleep when idle", _enabled),
            Divider(),
            Row("Sleep after", WithUnit(_idleMinutes, "minutes")),
            Divider(),
            Row("Start with Windows", _startup)),
        _startupProblem,
        Header("Stay awake while"),
        Card(spacing: 0,
            Row("Audio is playing", _audio),
            Row("An app is using the microphone", _microphone),
            Padded(_ignoredAudioApps.View),
            AudioActivityLink(),
            Divider(),
            Row("A game controller is in use", _controller),
            Divider(),
            Row("An app keeps the screen on", _display),
            Divider(),
            Padded(_processes.View)),
        Note("Using the keyboard or mouse always keeps the PC awake."));

    private UIElement BuildAdvancedPage()
    {
        var files = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        files.Children.Add(FileLink("Open settings file", AppPaths.ConfigFile));
        files.Children.Add(FileLink("Open log file", AppPaths.LogFile));
        return Page("Advanced",
            Header("Sleep"),
            Card(spacing: 0, Row("Warn before sleeping", WithUnit(_warningSeconds, "seconds"))),
            Header("Audio"),
            Card(spacing: 0, Row("Audio sensitivity", WithUnit(_thresholdDb, "dB"))),
            Note("Quieter sounds than this don't count as audio playing. Use \"Show audio activity\" on the Settings page to see levels."),
            Header("Keyboard and mouse"),
            Card(spacing: 0,
                Row("Ignore software-generated input", _ignoreInjected),
                Divider(),
                Padded(_ignoredInputDevices.View)),
            Header("Files"),
            files);
    }

    // ---- Live status -------------------------------------------------------------------------

    private void Refresh()
    {
        bool running = Program.EngineIsRunning();
        _notRunning.IsOpen = !running;

        EngineStatus? status = StatusFile.Read();
        bool fresh = status is not null && DateTime.UtcNow - status.UpdatedUtc < TimeSpan.FromSeconds(5);

        if (!running || !fresh || status is null)
        {
            ShowStatus(running ? "Starting…" : "Not running", running ? "" : "Start Drowsy to see what's keeping the PC awake.",
                Brush("SystemFillColorNeutralBrush", Colors.Gray), [], null, null);
        }
        else
        {
            Brush dot = status.State switch
            {
                "Awake" => Brush("SystemFillColorSuccessBrush", Colors.SeaGreen),
                "SleepingSoon" => Brush("SystemFillColorCautionBrush", Colors.Orange),
                "Paused" or "Disabled" => Brush("SystemFillColorNeutralBrush", Colors.Gray),
                _ => Brush("AccentFillColorDefaultBrush", Colors.DodgerBlue),
            };
            double? progress = status.SleepInSeconds is not null && status.LimitSeconds > 0
                ? Math.Clamp(100.0 * status.IdleSeconds / status.LimitSeconds, 0, 100)
                : null;
            string? last = status.State is "Idle" or "SleepingSoon" && status.LastActivity is { } la ? $"Last activity: {la}" : null;
            // Keep the master switch in sync if it was flipped from the tray menu.
            bool engineEnabled = status.State != "Disabled";
            if (engineEnabled != _enabled.IsOn && !_saveTimer.IsRunning)
            {
                _loading = true;
                _enabled.IsOn = engineEnabled;
                _loading = false;
            }

            // When awake the reasons list says it all; avoid repeating it in the headline.
            string headline = status.State == "Awake" ? "" : status.Headline;
            ShowStatus(status.Title, headline, dot, status.Reasons, progress, last);
        }

        if (_ticks++ % 5 == 0)
        {
            RenderRecentActivity();
            if (_historyPage.ReloadIfChanged()) RenderLastSleep();
        }
        if (ReferenceEquals(_nav.SelectedItem, _logItem)) _logPage.ReloadIfChanged();
    }

    private void ShowStatus(string title, string headline, Brush dot, List<string> reasons, double? progress, string? last)
    {
        _title.Text = title;
        _stateDot.Fill = dot;
        _headline.Text = headline;
        _headline.Visibility = headline.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        _reasons.Children.Clear();
        foreach (string reason in reasons)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new Ellipse
            {
                Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center,
                Fill = Brush("SystemFillColorSuccessBrush", Colors.SeaGreen),
            });
            row.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap });
            _reasons.Children.Add(row);
        }
        _reasons.Visibility = reasons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _progress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        _progress.Value = progress ?? 0;

        _lastActivity.Text = last ?? "";
        _lastActivity.Visibility = last is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderLastSleep()
    {
        _lastSleep.Children.Clear();
        if (_historyPage.Events.FirstOrDefault() is { } last) _lastSleep.Children.Add(HistoryPage.EventRow(last));
        else _lastSleep.Children.Add(Padded(Note("Drowsy hasn't put the PC to sleep yet.")));
    }

    private void RenderRecentActivity()
    {
        _recentActivity.Children.Clear();
        foreach (string line in RecentLogLines(8))
        {
            _recentActivity.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        }
        if (_recentActivity.Children.Count == 0) _recentActivity.Children.Add(Note("Nothing yet."));
    }

    // "2026-11-12 01:22:58 INFO  Activity: Keyboard/mouse after 3m quiet — mouse: \\?\HID#VID_1532..."
    //   -> "01:22  Keyboard/mouse after 3m quiet — mouse: VID_1532&PID_00C1"
    private static List<string> RecentLogLines(int count)
    {
        string[] interesting = ["Activity:", "Sleeping", "Resumed", "Paused", "Pause ended"];
        var result = new List<string>();
        string[] lines = LogFile.ReadLines(AppPaths.LogFile);
        for (int i = lines.Length - 1; i >= 0 && result.Count < count; i--)
        {
            if (!LogFile.TryParse(lines[i], out DateTime time, out bool error, out string message)) continue;
            if (!error && !interesting.Any(k => message.Contains(k, StringComparison.Ordinal))) continue;
            message = LogFile.ShortenDevicePaths(message.Replace("Activity: ", "", StringComparison.Ordinal));
            result.Add($"{time:HH:mm}  {(error ? "Error: " : "")}{message}");
        }
        return result;
    }

    // ---- Load / save -------------------------------------------------------------------------

    private void LoadSettings()
    {
        _loading = true;
        _enabled.IsOn = _config.Enabled;
        _idleMinutes.Value = _config.IdleMinutes;
        _startup.IsOn = Startup.IsEnabled;
        _audio.IsOn = _config.AudioIsActivity;
        _microphone.IsOn = _config.MicrophoneIsActivity;
        _controller.IsOn = _config.ControllerIsActivity;
        _display.IsOn = _config.HonorDisplayRequests;
        _warningSeconds.Value = _config.WarningSeconds;
        _thresholdDb.Value = Math.Round(Math.Clamp(20 * Math.Log10(Math.Max(_config.AudioPeakThreshold, 1e-9)), MinDb, MaxDb));
        _processes.SetValues(_config.BlockingProcesses);
        _ignoredAudioApps.SetValues(_config.IgnoredAudioApps);
        _ignoreInjected.IsOn = _config.IgnoreInjectedInput;
        _checkUpdates.IsOn = _config.CheckForUpdates;
        _ignoredInputDevices.SetValues(_config.IgnoredInputDevices);
        _loading = false;
        _ = LearnFriendlyNamesAsync();
    }

    /// <summary>Show device/app names (rather than VID/PIDs and exe names) for already-chosen items.</summary>
    private async Task LearnFriendlyNamesAsync()
    {
        try
        {
            _ignoredInputDevices.Learn(await Task.Run(Catalog.InputDevices));
            List<Candidate> apps = await Task.Run(Catalog.RunningApps);
            _processes.Learn(apps);
            _ignoredAudioApps.Learn(apps);
        }
        catch (Exception ex)
        {
            Log.Error("Settings: could not look up friendly names", ex);
        }
    }

    private void HookAutoSave()
    {
        foreach (NumberBox n in new[] { _idleMinutes, _warningSeconds, _thresholdDb }) n.ValueChanged += (_, _) => ScheduleSave();
        foreach (ToggleSwitch t in new[] { _enabled, _audio, _microphone, _controller, _display, _ignoreInjected, _checkUpdates }) t.Toggled += (_, _) => ScheduleSave();
        foreach (PickerList p in new[] { _processes, _ignoredAudioApps, _ignoredInputDevices }) p.Changed += ScheduleSave;
        _ignoredAudioApps.Changed += () => _audioMonitor?.Invalidate();
        _startup.Toggled += (_, _) =>
        {
            if (_loading || _startup.IsOn == Startup.IsEnabled) return;
            try
            {
                Startup.Set(_startup.IsOn);
                _startupProblem.IsOpen = false;
            }
            catch (InvalidOperationException ex)
            {
                _loading = true;
                _startup.IsOn = false;
                _loading = false;
                _startupProblem.Message = ex.Message;
                _startupProblem.IsOpen = true;
            }
        };
    }

    private void ScheduleSave()
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        try
        {
            ConfigStore.Save(BuildConfig());
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
        }
    }

    /// <summary>The settings as currently shown (also drives the Audio activity window live).</summary>
    private Config BuildConfig()
    {
        return new Config
        {
            Enabled = _enabled.IsOn,
            IdleMinutes = (int)Value(_idleMinutes, 20),
            WarningSeconds = (int)Value(_warningSeconds, 60),
            AudioIsActivity = _audio.IsOn,
            MicrophoneIsActivity = _microphone.IsOn,
            ControllerIsActivity = _controller.IsOn,
            HonorDisplayRequests = _display.IsOn,
            AudioPeakThreshold = (float)Math.Pow(10, Value(_thresholdDb, -60) / 20),
            AudioMinSeconds = _config.AudioMinSeconds,
            BlockingProcesses = _processes.Values,
            IgnoredAudioApps = _ignoredAudioApps.Values,
            IgnoreInjectedInput = _ignoreInjected.IsOn,
            CheckForUpdates = _checkUpdates.IsOn,
            IgnoredInputDevices = _ignoredInputDevices.Values,
        };
    }

    private FrameworkElement AudioActivityLink()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = "", FontSize = 14 }); // equalizer-style icon
        content.Children.Add(new TextBlock { Text = "Show audio activity" });
        var link = new HyperlinkButton { Content = content, Margin = new Thickness(-12, -4, 0, 6) };
        link.Click += (_, _) => OpenAudioMonitor();
        return link;
    }

    internal void OpenAudioMonitor()
    {
        if (_audioMonitor is null)
        {
            _audioMonitor = new AudioMonitorWindow(BuildConfig, (app, ignore) => _ignoredAudioApps.SetIncluded(app, ignore));
            _audioMonitor.Closed += (_, _) => _audioMonitor = null;
        }
        _audioMonitor.Activate();
    }

    private static double Value(NumberBox box, double fallback) => double.IsNaN(box.Value) ? fallback : Math.Round(box.Value);

    // ---- Helpers -----------------------------------------------------------------------------

    private void SizeAndCenter(int width, int height)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = Native.GetDpiForWindow(hwnd) / 96.0;
        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var size = new SizeInt32((int)(width * scale), Math.Min((int)(height * scale), area.WorkArea.Height - 40));
        AppWindow.Resize(size);
        AppWindow.Move(new PointInt32(
            area.WorkArea.X + (area.WorkArea.Width - size.Width) / 2,
            area.WorkArea.Y + (area.WorkArea.Height - size.Height) / 2));
    }

    /// <summary>Whole numbers only, always within [min, max]; invalid or empty input reverts.</summary>
    private static NumberBox NewNumberBox(double min, double max, double small, double large)
    {
        var box = new NumberBox
        {
            Minimum = min,
            Maximum = max,
            SmallChange = small,
            LargeChange = large,
            // No spin buttons: type a value, or use the arrow keys / mouse wheel.
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
            {
                IntegerDigits = 1,
                FractionDigits = 0,
                NumberRounder = new Windows.Globalization.NumberFormatting.IncrementNumberRounder
                {
                    Increment = 1,
                    RoundingAlgorithm = Windows.Globalization.NumberFormatting.RoundingAlgorithm.RoundHalfUp,
                },
            },
            Width = 80,
        };
        box.ValueChanged += (sender, e) =>
        {
            double v = e.NewValue;
            double fallback = double.IsNaN(e.OldValue) ? min : e.OldValue;
            double corrected = double.IsNaN(v) ? fallback : Math.Clamp(Math.Round(v), min, max);
            if (!corrected.Equals(v)) sender.Value = corrected;
        };
        return box;
    }

    private static ToggleSwitch NewToggle() => new() { OnContent = "", OffContent = "", MinWidth = 0 };
}
