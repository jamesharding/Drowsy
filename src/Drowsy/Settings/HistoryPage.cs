using Drowsy.Activity;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Drowsy.Settings.Ui;

namespace Drowsy.Settings;

/// <summary>Every time the PC went to sleep, newest first, grouped by day.</summary>
internal sealed class HistoryPage
{
    private readonly StackPanel _days = new() { Spacing = 8 };
    private readonly TextBlock _summary = Note("");
    private DateTime _stamp;

    public HistoryPage()
    {
        View = Page("Sleep history", _summary, _days);
    }

    public UIElement View { get; }

    /// <summary>Newest first.</summary>
    public List<SleepEvent> Events { get; private set; } = [];

    /// <summary>Re-reads the history when the engine has written to it. Returns true if it changed.</summary>
    public bool ReloadIfChanged()
    {
        // Before the engine has created the history file, it is rebuilt from the log.
        string source = File.Exists(SleepHistory.Path) ? SleepHistory.Path : AppPaths.LogFile;
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(source); }
        catch { return false; }
        if (stamp == _stamp) return false;
        _stamp = stamp;

        Events = SleepHistory.Load().Events.OrderByDescending(e => e.SleptUtc).ToList();
        Render();
        return true;
    }

    private void Render()
    {
        _days.Children.Clear();
        _summary.Text = Summary(Events);
        if (Events.Count == 0)
        {
            _days.Children.Add(Note("Drowsy hasn't put the PC to sleep yet."));
            return;
        }

        foreach (IGrouping<DateTime, SleepEvent> day in Events.GroupBy(e => e.SleptUtc.ToLocalTime().Date))
        {
            _days.Children.Add(Header(DayLabel(day.Key)));
            var rows = new List<UIElement>();
            foreach (SleepEvent e in day)
            {
                if (rows.Count > 0) rows.Add(Divider());
                rows.Add(EventRow(e));
            }
            _days.Children.Add(Card(0, [.. rows]));
        }
    }

    private static string Summary(List<SleepEvent> events)
    {
        DateTime since = DateTime.UtcNow.AddDays(-7);
        List<SleepEvent> week = events.Where(e => e.SleptUtc >= since).ToList();
        int byDrowsy = week.Count(e => !e.Failed && e.Trigger != SleepTrigger.Other);
        int other = week.Count(e => e.Trigger == SleepTrigger.Other);
        int failed = week.Count(e => e.Failed);

        string text = byDrowsy switch
        {
            0 => "Drowsy hasn't put the PC to sleep in the last 7 days.",
            1 => "Drowsy put the PC to sleep once in the last 7 days.",
            _ => $"Drowsy put the PC to sleep {byDrowsy} times in the last 7 days.",
        };
        if (other > 0) text += other == 1 ? " It also slept once by other means." : $" It also slept {other} times by other means.";
        if (failed > 0) text += failed == 1 ? " One sleep request was refused by Windows." : $" {failed} sleep requests were refused by Windows.";
        return text;
    }

    /// <summary>One sleep: what happened, why, and when it woke. Also used on the Status page.</summary>
    public static FrameworkElement EventRow(SleepEvent e)
    {
        DateTime slept = e.SleptUtc.ToLocalTime();
        string title;
        var details = new List<string>();

        if (e.Failed)
        {
            title = "Couldn't sleep";
            details.Add($"Windows refused the request (error {e.Error ?? 0}). Drowsy waits 5 minutes before trying again.");
        }
        else
        {
            title = e.Trigger switch
            {
                SleepTrigger.Idle when e.IdleSeconds is { } s => $"Slept after {Format.Countdown(s * 1000)} idle",
                SleepTrigger.Idle => "Slept after being idle",
                SleepTrigger.Manual => "Slept: you chose Sleep now",
                _ => "Slept (not started by Drowsy)",
            };
            if (e.WokeUtc is { } wokeUtc)
            {
                DateTime woke = wokeUtc.ToLocalTime();
                string when = woke.Date == slept.Date ? $"{woke:HH:mm}" : $"{DayLabel(woke)} {woke:HH:mm}";
                details.Add($"Woke at {when} · asleep {Format.Duration((long)(wokeUtc - e.SleptUtc).TotalMilliseconds)}");
            }
            else
            {
                details.Add("Wake time not recorded");
            }
        }

        if (e.Trigger == SleepTrigger.Idle && e.IdleSinceUtc is { } sinceUtc)
        {
            string at = $"{sinceUtc.ToLocalTime():HH:mm}";
            if (e.LastActivity is { } last) details.Add($"Last activity: {last} at {at}");
            else if (e.IdleSinceReason is { } reason) details.Add($"No activity since {at} ({reason})");
        }

        var grid = new Grid { ColumnSpacing = 14, Padding = new Thickness(0, 10, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = e.Failed ? "" : "", // warning / moon
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = e.Failed ? Brush("SystemFillColorCautionBrush", Colors.Orange)
                : e.Trigger == SleepTrigger.Other ? Brush("TextFillColorTertiaryBrush", Colors.Gray)
                : Brush("AccentTextFillColorPrimaryBrush", Colors.DodgerBlue),
        };

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        foreach (string d in details)
        {
            text.Children.Add(new TextBlock
            {
                Text = d,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = Brush("TextFillColorSecondaryBrush", Colors.Gray),
            });
        }
        Grid.SetColumn(text, 1);

        var time = new TextBlock
        {
            Text = $"{slept:HH:mm}",
            Foreground = Brush("TextFillColorSecondaryBrush", Colors.Gray),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(time, 2);

        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(time);
        return grid;
    }
}
