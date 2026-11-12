using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using static Drowsy.Settings.Ui;

namespace Drowsy.Settings;

/// <summary>The engine's log, newest first, with a filter and search. Refreshes while visible.</summary>
internal sealed class LogPage
{
    private const int MaxRows = 400;

    private static readonly string[] SleepWords =
        ["Sleeping", "System suspending", "Resumed", "SetSuspendState", "Paused", "Pause ended", "Started.", "Exit requested", "from tray menu"];

    private readonly record struct Line(DateTime? Time, bool Error, string Message, string Raw);

    private readonly ComboBox _filter = new() { Width = 160 };
    private readonly TextBox _search = new() { PlaceholderText = "Search the log" };
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly TextBlock _summary = Note("");
    private readonly DispatcherQueueTimer _searchTimer;
    private List<Line> _lines = [];
    private List<Line> _shown = [];
    private (long Length, DateTime Write) _stamp;

    public LogPage()
    {
        _filter.Items.Add("Everything");
        _filter.Items.Add("Sleep and wake");
        _filter.Items.Add("Activity");
        _filter.Items.Add("Errors");
        _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => Render();

        _searchTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(250);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => Render();
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };

        var copy = new Button { Content = "Copy" };
        ToolTipService.SetToolTip(copy, "Copy the lines shown");
        copy.Click += (_, _) => Copy();
        var open = new Button { Content = "Open file" };
        ToolTipService.SetToolTip(open, AppPaths.LogFile);
        open.Click += (_, _) => TrayApp.OpenFile(AppPaths.LogFile);

        var toolbar = new Grid { ColumnSpacing = 8 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Add(toolbar, _filter, 0);
        Add(toolbar, _search, 1);
        Add(toolbar, copy, 2);
        Add(toolbar, open, 3);

        var list = new Border
        {
            Child = new ScrollViewer { Content = _rows, Padding = new Thickness(16, 4, 16, 8) },
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = Brush("CardBackgroundFillColorDefaultBrush", Colors.Transparent),
            BorderBrush = Brush("CardStrokeColorDefaultBrush", Colors.Transparent),
        };

        var root = new Grid { RowSpacing = 10, Padding = new Thickness(28, 16, 28, 20), MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int i = 0; i < 3; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        TextBlock title = PageTitle("Log");
        Grid.SetRow(title, 0);
        Grid.SetRow(toolbar, 1);
        Grid.SetRow(_summary, 2);
        Grid.SetRow(list, 3);
        root.Children.Add(title);
        root.Children.Add(toolbar);
        root.Children.Add(_summary);
        root.Children.Add(list);
        View = root;
    }

    public UIElement View { get; }

    /// <summary>Re-reads the log if it changed since last time (cheap when it hasn't).</summary>
    public void ReloadIfChanged()
    {
        (long, DateTime) stamp;
        try
        {
            var file = new FileInfo(AppPaths.LogFile);
            stamp = file.Exists ? (file.Length, file.LastWriteTimeUtc) : (0, default);
        }
        catch
        {
            return;
        }
        if (stamp == _stamp && _lines.Count > 0) return;
        _stamp = stamp;

        var lines = new List<Line>();
        foreach (string path in new[] { AppPaths.LogFile + ".old", AppPaths.LogFile })
        {
            foreach (string raw in LogFile.ReadLines(path))
            {
                bool parsed = LogFile.TryParse(raw, out DateTime time, out bool error, out string message);
                lines.Add(new Line(parsed ? time : null, error, LogFile.ShortenDevicePaths(message), raw));
            }
        }
        _lines = lines;
        Render();
    }

    private void Render()
    {
        string query = _search.Text.Trim();
        int filter = _filter.SelectedIndex;
        List<Line> matching = _lines.Where(l =>
                filter switch
                {
                    1 => SleepWords.Any(w => l.Message.Contains(w, StringComparison.Ordinal)),
                    2 => l.Message.StartsWith("Activity:", StringComparison.Ordinal),
                    3 => l.Error,
                    _ => true,
                } &&
                (query.Length == 0 || l.Raw.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        _shown = matching.TakeLast(MaxRows).ToList();

        _summary.Text = matching.Count switch
        {
            0 when _lines.Count == 0 => "The log is empty.",
            0 => "No matching lines.",
            > MaxRows => $"Showing the latest {MaxRows} of {matching.Count} lines. Open the file to see everything.",
            1 => "1 line.",
            _ => $"{matching.Count} lines, newest first.",
        };

        _rows.Children.Clear();
        DateTime? day = null;
        for (int i = _shown.Count - 1; i >= 0; i--)
        {
            Line line = _shown[i];
            if (line.Time is { } t && t.Date != day)
            {
                day = t.Date;
                _rows.Children.Add(new TextBlock
                {
                    Text = DayLabel(t),
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, _rows.Children.Count == 0 ? 6 : 14, 0, 4),
                });
            }
            _rows.Children.Add(LineRow(line));
        }
    }

    private static Grid LineRow(Line line)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var time = new TextBlock
        {
            Text = line.Time is { } t ? $"{t:HH:mm:ss}" : "",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Brush("TextFillColorTertiaryBrush", Colors.Gray),
            Margin = new Thickness(0, 1, 0, 0),
        };
        bool sleep = line.Message.StartsWith("Sleeping:", StringComparison.Ordinal) || line.Message is "Resumed" or "Resumed (user)";
        var message = new TextBlock
        {
            Text = line.Error ? "Error: " + line.Message : line.Message,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = 13,
            FontWeight = sleep || line.Error ? FontWeights.SemiBold : FontWeights.Normal,
        };
        if (line.Error) message.Foreground = Brush("SystemFillColorCriticalBrush", Colors.IndianRed);
        Grid.SetColumn(message, 1);
        grid.Children.Add(time);
        grid.Children.Add(message);
        return grid;
    }

    private void Copy()
    {
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, _shown.Select(l => l.Raw)));
        Clipboard.SetContent(package);
    }

    private static void Add(Grid grid, FrameworkElement element, int column)
    {
        element.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
