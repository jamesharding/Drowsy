using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Drowsy.Activity;

namespace Drowsy.Settings;

/// <summary>Something the user can pick: the stored <see cref="Value"/> plus friendly text.</summary>
internal readonly record struct Candidate(string Value, string Title, string? Subtitle);

/// <summary>
/// A list of chosen items (each with a remove button) and an "Add" button that opens a searchable
/// picker. Typing a name that isn't listed and pressing Enter adds it as-is.
/// </summary>
internal sealed class PickerList
{
    private readonly List<string> _values = [];
    private readonly Dictionary<string, Candidate> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<List<Candidate>> _load;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _empty;
    private readonly Flyout _flyout = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
    private readonly TextBox _search = new() { PlaceholderText = "Search, or type a name and press Enter" };
    private readonly ListView _list = new()
    {
        MaxHeight = 320,
        IsItemClickEnabled = true,
        SelectionMode = ListViewSelectionMode.None,
    };
    private readonly ProgressRing _loading = new() { IsActive = true, Width = 24, Height = 24, Margin = new Thickness(0, 12, 0, 12) };
    private List<Candidate> _candidates = [];

    public PickerList(string label, string emptyText, string addLabel, Func<List<Candidate>> load)
    {
        _load = load;
        _empty = new TextBlock { Text = emptyText, Opacity = 0.6, Margin = new Thickness(0, 2, 0, 2) };

        var flyoutContent = new StackPanel { Width = 340, Spacing = 8 };
        flyoutContent.Children.Add(_search);
        flyoutContent.Children.Add(_loading);
        flyoutContent.Children.Add(_list);
        _flyout.Content = flyoutContent;
        _flyout.Opening += async (_, _) => await OnOpeningAsync();

        _search.TextChanged += (_, _) => RenderCandidates();
        _search.KeyDown += OnSearchKeyDown;
        _list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is FrameworkElement { Tag: string value }) Add(value);
        };

        var addContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        addContent.Children.Add(new FontIcon { Glyph = "", FontSize = 12 });
        addContent.Children.Add(new TextBlock { Text = addLabel });
        var add = new Button { Content = addContent, Flyout = _flyout, Margin = new Thickness(0, 4, 0, 0) };

        var view = new StackPanel { Spacing = 4 };
        view.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        view.Children.Add(_rows);
        view.Children.Add(_empty);
        view.Children.Add(add);
        View = view;
    }

    public event Action? Changed;

    public FrameworkElement View { get; }

    public List<string> Values => [.. _values];

    /// <summary>Add or remove a value programmatically (raises <see cref="Changed"/>).</summary>
    public void SetIncluded(string value, bool included)
    {
        if (included) Add(value);
        else Remove(value);
    }

    public void SetValues(IEnumerable<string> values)
    {
        _values.Clear();
        _values.AddRange(values);
        RenderRows();
    }

    /// <summary>Teach the list friendly names for stored values (e.g. a device name for a VID/PID).</summary>
    public void Learn(IEnumerable<Candidate> candidates)
    {
        foreach (Candidate c in candidates) _known[c.Value] = c;
        RenderRows();
    }

    private async Task OnOpeningAsync()
    {
        _search.Text = "";
        _list.Items.Clear();
        _loading.Visibility = Visibility.Visible;
        try
        {
            _candidates = await Task.Run(_load);
        }
        catch (Exception ex)
        {
            Log.Error("Picker: could not load choices", ex);
            _candidates = [];
        }
        _loading.Visibility = Visibility.Collapsed;
        Learn(_candidates);
        RenderCandidates();
        _search.Focus(FocusState.Programmatic);
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        string text = _search.Text.Trim();
        if (text.Length == 0) return;

        // Exact match on name or value, else the only remaining result, else the typed text as-is.
        List<Candidate> filtered = Filtered().ToList();
        int exact = filtered.FindIndex(c =>
            c.Value.Equals(text, StringComparison.OrdinalIgnoreCase) || c.Title.Equals(text, StringComparison.OrdinalIgnoreCase));
        string value = exact >= 0 ? filtered[exact].Value
            : filtered.Count == 1 ? filtered[0].Value
            : text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? text[..^4] : text;

        Add(value);
        e.Handled = true;
    }

    private void Add(string value)
    {
        _flyout.Hide();
        if (_values.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        _values.Add(value);
        RenderRows();
        Changed?.Invoke();
    }

    private void Remove(string value)
    {
        _values.RemoveAll(v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
        RenderRows();
        Changed?.Invoke();
    }

    private IEnumerable<Candidate> Filtered()
    {
        string q = _search.Text.Trim();
        return _candidates
            .Where(c => !_values.Contains(c.Value, StringComparer.OrdinalIgnoreCase))
            .Where(c => q.Length == 0
                || c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Value.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void RenderCandidates()
    {
        _list.Items.Clear();
        foreach (Candidate c in Filtered().Take(200))
        {
            var item = new StackPanel { Tag = c.Value, Padding = new Thickness(0, 4, 0, 4) };
            item.Children.Add(new TextBlock { Text = c.Title, TextTrimming = TextTrimming.CharacterEllipsis });
            if (c.Subtitle is { Length: > 0 } sub)
            {
                item.Children.Add(new TextBlock { Text = sub, FontSize = 12, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
            }
            _list.Items.Add(item);
        }
    }

    private void RenderRows()
    {
        _rows.Children.Clear();
        foreach (string value in _values)
        {
            string title = _known.TryGetValue(value, out Candidate c) ? c.Title : value;
            string? sub = _known.TryGetValue(value, out Candidate k) && !k.Title.Equals(value, StringComparison.OrdinalIgnoreCase) ? value : null;

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis });
            if (sub is not null) text.Children.Add(new TextBlock { Text = sub, FontSize = 12, Opacity = 0.65 });

            var remove = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 11 },
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(remove, "Remove");
            remove.Click += (_, _) => Remove(value);

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(remove, 1);
            row.Children.Add(text);
            row.Children.Add(remove);
            _rows.Children.Add(row);
        }
        _empty.Visibility = _values.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>Sources of choices for the pickers.</summary>
internal static class Catalog
{
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "Memory Compression", "Secure System", "Drowsy",
    };

    /// <summary>Running programs: apps with windows first (with their friendly names), then background processes.</summary>
    public static List<Candidate> RunningApps()
    {
        var apps = new Dictionary<string, (bool Window, string? Description)>(StringComparer.OrdinalIgnoreCase);
        foreach (Process p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    string name = p.ProcessName;
                    if (Hidden.Contains(name)) continue;
                    bool window = p.MainWindowHandle != 0;
                    apps.TryGetValue(name, out var existing);
                    string? description = existing.Description;
                    if (window && description is null)
                    {
                        try { description = p.MainModule?.FileVersionInfo.FileDescription; } catch { }
                    }
                    apps[name] = (existing.Window || window, string.IsNullOrWhiteSpace(description) ? null : description.Trim());
                }
                catch
                {
                    // process exited or is protected
                }
            }
        }

        return apps
            .OrderByDescending(a => a.Value.Window)
            .ThenBy(a => a.Value.Description ?? a.Key, StringComparer.OrdinalIgnoreCase)
            .Select(a => new Candidate(
                a.Key,
                a.Value.Description ?? a.Key,
                a.Value.Window ? (a.Value.Description is null ? null : a.Key + ".exe") : "Background process"))
            .ToList();
    }

    /// <summary>Apps with an audio session first, then other running apps with windows.</summary>
    public static List<Candidate> AudioApps()
    {
        List<string> withAudio = AudioSource.ListSessionApps();
        List<Candidate> running = RunningApps();
        var result = new List<Candidate>();
        foreach (string app in withAudio)
        {
            int i = running.FindIndex(c => c.Value.Equals(app, StringComparison.OrdinalIgnoreCase));
            string title = i >= 0 ? running[i].Title : app;
            result.Add(new Candidate(app, title, "Uses audio"));
        }
        result.AddRange(running.Where(c => c.Subtitle != "Background process" && !withAudio.Contains(c.Value, StringComparer.OrdinalIgnoreCase)));
        return result;
    }

    /// <summary>Connected keyboards and mice, by product name.</summary>
    public static List<Candidate> InputDevices() =>
        RawInputSource.ListDevices().Select(d => new Candidate(d.Key, d.Name, $"{d.Kind} · {d.Key.Replace("&", "  ")}")).ToList();
}
