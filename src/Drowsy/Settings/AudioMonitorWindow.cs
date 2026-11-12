using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Drowsy.Activity;
using Drowsy.Interop;
using Windows.Graphics;

namespace Drowsy.Settings;

/// <summary>
/// Live per-app audio meters. Runs the same detection logic as the engine (per-app sessions,
/// threshold, "audible for N seconds within 10 s") against the settings currently shown in the
/// main window, so you can see exactly which apps would keep the PC awake and why.
/// </summary>
internal sealed partial class AudioMonitorWindow : Window
{
    private const double MeterWidth = 150;
    private const double FloorDb = -80; // left edge of the meter
    private const int FrameMs = 60;

    private readonly Func<Config> _config;
    private readonly Action<string, bool> _setIgnored;
    private readonly AudioSource _audio = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _empty = new() { Text = "No apps are using audio right now.", Opacity = 0.6, Margin = new Thickness(4, 8, 0, 8) };
    private readonly Dictionary<string, Row> _byApp = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _friendly = new(StringComparer.OrdinalIgnoreCase);
    private string _order = "";
    private int _frame;

    private sealed class Row
    {
        public required Grid View { get; init; }
        public required TextBlock Status { get; init; }
        public required Rectangle Fill { get; init; }
        public required Rectangle Threshold { get; init; }
        public required Button Toggle { get; init; }
        public required TextBlock Name { get; init; }
        public double Level { get; set; }
    }

    public AudioMonitorWindow(Func<Config> config, Action<string, bool> setIgnored)
    {
        _config = config;
        _setIgnored = setIgnored;

        Title = "Audio activity";
        SystemBackdrop = new MicaBackdrop();
        Ui.SetAppIcon(AppWindow);
        Resize(500, 520);

        var page = new StackPanel { Spacing = 10, Padding = new Thickness(20, 12, 20, 20) };
        page.Children.Add(new TextBlock { Text = "Audio activity", FontSize = 20, FontWeight = FontWeights.SemiBold });
        page.Children.Add(new TextBlock
        {
            Text = "Every app that has audio open. An app keeps the PC awake once its level has been above the " +
                   "line for a few seconds; short sounds like notifications don't count.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brush("TextFillColorSecondaryBrush", Colors.Gray),
        });
        var card = new Border
        {
            Child = new StackPanel { Children = { _rows, _empty } },
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = Brush("CardBackgroundFillColorDefaultBrush", Colors.Transparent),
            BorderBrush = Brush("CardStrokeColorDefaultBrush", Colors.Transparent),
        };
        page.Children.Add(card);
        Content = new ScrollViewer { Content = page };

        _ = LoadFriendlyNamesAsync();

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(FrameMs);
        _timer.Tick += (_, _) => OnFrame();
        _timer.Start();

        Closed += (_, _) =>
        {
            _timer.Stop();
            _audio.Dispose();
        };
    }

    /// <summary>Call when the ignore list changes so meters re-read who is ignored.</summary>
    public void Invalidate() => _audio.Invalidate();

    private async Task LoadFriendlyNamesAsync()
    {
        try
        {
            List<Candidate> apps = await Task.Run(Catalog.RunningApps);
            _friendly = apps.ToDictionary(a => a.Value, a => a.Title, StringComparer.OrdinalIgnoreCase);
            foreach ((string app, Row row) in _byApp) row.Name.Text = Friendly(app);
        }
        catch
        {
            // names are cosmetic
        }
    }

    private string Friendly(string app) => _friendly.TryGetValue(app, out string? f) ? f : app;

    private void OnFrame()
    {
        Config config = _config();

        // Bookkeeping at the engine's sample rate; meters in between for smooth movement.
        if (++_frame * FrameMs % AudioSource.SampleIntervalMs < FrameMs) _audio.Sample(config);
        else _audio.ReadPeaks(config);

        List<AppAudio> apps = _audio.Snapshot(config)
            .OrderByDescending(a => !a.Ignored && (a.Triggering || a.MicActive))
            .ThenBy(a => a.Ignored)
            .ThenBy(a => Friendly(a.App), StringComparer.OrdinalIgnoreCase)
            .ToList();

        string order = string.Join("|", apps.Select(a => a.App));
        if (order != _order)
        {
            _order = order;
            RebuildRows(apps);
        }

        double thresholdX = ToX(config.AudioPeakThreshold);
        long needMs = Math.Max(0, config.AudioMinSeconds) * 1000L;
        foreach (AppAudio a in apps)
        {
            Row row = _byApp[a.App];

            // Fast attack, gentle fall-off so the meter is readable.
            double target = ToX(a.Peak);
            row.Level = target > row.Level ? target : Math.Max(target, row.Level - 4);
            row.Fill.Width = row.Level;
            row.Threshold.Margin = new Thickness(thresholdX, 0, 0, 0);

            bool loud = a.Peak > config.AudioPeakThreshold;
            row.Fill.Fill = a.Ignored ? Brush("ControlStrongFillColorDisabledBrush", Colors.Gray)
                : a.Triggering ? Brush("SystemFillColorSuccessBrush", Colors.SeaGreen)
                : loud ? Brush("AccentFillColorDefaultBrush", Colors.DodgerBlue)
                : Brush("ControlStrongFillColorDefaultBrush", Colors.Gray);

            bool micKeepsAwake = a.MicActive && !a.Ignored && config.MicrophoneIsActivity;
            bool keepsAwake = (a.Triggering && config.AudioIsActivity) || micKeepsAwake;
            string mic = a.MicActive ? "Using the microphone" : "";
            row.Status.Text = a.Ignored ? (a.MicActive ? "Ignored (using the microphone)" : "Ignored")
                : a.Triggering ? (a.MicActive ? "Playing and using the microphone — keeping the PC awake" : "Playing — keeping the PC awake")
                : micKeepsAwake ? "Using the microphone — keeping the PC awake"
                : a.AudibleMs > 0 ? $"Playing — counts after {Format.Duration(Math.Max(0, needMs - a.AudibleMs))}"
                : mic.Length > 0 ? mic
                : "Quiet";
            row.Status.Foreground = keepsAwake
                ? Brush("SystemFillColorSuccessBrush", Colors.SeaGreen)
                : Brush("TextFillColorSecondaryBrush", Colors.Gray);
            row.Name.Opacity = a.Ignored ? 0.6 : 1;
            row.Toggle.Content = a.Ignored ? "Count" : "Ignore";
        }
        _empty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildRows(List<AppAudio> apps)
    {
        _rows.Children.Clear();
        foreach (AppAudio a in apps)
        {
            if (!_byApp.TryGetValue(a.App, out Row? row))
            {
                row = CreateRow(a.App);
                _byApp[a.App] = row;
            }
            _rows.Children.Add(row.View);
        }
    }

    private Row CreateRow(string app)
    {
        var name = new TextBlock { Text = Friendly(app), TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
        var status = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(status);

        var track = new Rectangle
        {
            Width = MeterWidth, Height = 8, RadiusX = 4, RadiusY = 4,
            Fill = Brush("ControlFillColorSecondaryBrush", Colors.DimGray),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var fill = new Rectangle { Width = 0, Height = 8, RadiusX = 4, RadiusY = 4, HorizontalAlignment = HorizontalAlignment.Left };
        var threshold = new Rectangle
        {
            Width = 2, Height = 16,
            Fill = Brush("TextFillColorPrimaryBrush", Colors.White),
            HorizontalAlignment = HorizontalAlignment.Left,
            Opacity = 0.8,
        };
        ToolTipService.SetToolTip(threshold, "Threshold");
        var meter = new Grid { Width = MeterWidth, VerticalAlignment = VerticalAlignment.Center };
        meter.Children.Add(track);
        meter.Children.Add(fill);
        meter.Children.Add(threshold);

        var toggle = new Button { MinWidth = 72, VerticalAlignment = VerticalAlignment.Center };
        toggle.Click += (_, _) =>
        {
            bool nowIgnored = (string)toggle.Content == "Ignore";
            _setIgnored(app, nowIgnored);
            _audio.Invalidate();
        };

        var grid = new Grid { ColumnSpacing = 14, Padding = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(meter, 1);
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(text);
        grid.Children.Add(meter);
        grid.Children.Add(toggle);

        return new Row { View = grid, Status = status, Fill = fill, Threshold = threshold, Toggle = toggle, Name = name };
    }

    /// <summary>Peak (0..1) to a pixel offset on a dB scale from <see cref="FloorDb"/> to 0 dBFS.</summary>
    private static double ToX(float peak)
    {
        double db = peak <= 0 ? FloorDb : 20 * Math.Log10(peak);
        return Math.Clamp((db - FloorDb) / -FloorDb, 0, 1) * MeterWidth;
    }

    private void Resize(int width, int height)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = Native.GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
    }

    private static Brush Brush(string key, Windows.UI.Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush b ? b : new SolidColorBrush(fallback);
}
