using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Drowsy.Settings;

/// <summary>
/// Small building blocks shared by the settings window's pages. Typography is set directly
/// (Windows type ramp) rather than via theme Style lookups, which don't resolve under NativeAOT.
/// </summary>
internal static class Ui
{
    /// <summary>A page: title plus content, scrolling vertically, kept to a readable width.</summary>
    public static ScrollViewer Page(string title, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(28, 16, 28, 28), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(PageTitle(title));
        foreach (UIElement child in children) panel.Children.Add(child);
        return new ScrollViewer { Content = panel };
    }

    public static TextBlock PageTitle(string text) => new()
    {
        Text = text,
        FontSize = 28,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 8),
    };

    public static Grid Row(string label, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 12, MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    public static StackPanel WithUnit(FrameworkElement control, string unit)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(control);
        panel.Children.Add(new TextBlock { Text = unit, Width = 56, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        return panel;
    }

    public static FrameworkElement Padded(FrameworkElement content)
    {
        content.Margin = new Thickness(0, 10, 0, 6);
        return content;
    }

    public static Border Card(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (UIElement child in children) panel.Children.Add(child);
        return new Border
        {
            Child = panel,
            Padding = new Thickness(16, 10, 16, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = Brush("CardBackgroundFillColorDefaultBrush", Colors.Transparent),
            BorderBrush = Brush("CardStrokeColorDefaultBrush", Colors.Transparent),
        };
    }

    public static Rectangle Divider() => new()
    {
        Height = 1,
        Fill = Brush("DividerStrokeColorDefaultBrush", Colors.Gray),
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    public static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(4, 12, 0, 0),
    };

    public static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Foreground = Brush("TextFillColorSecondaryBrush", Colors.Gray),
        Margin = new Thickness(4, 0, 0, 0),
    };

    /// <summary>A link-style button; nudged left so its text lines up with surrounding content.</summary>
    public static HyperlinkButton Link(string text, Action onClick)
    {
        var link = new HyperlinkButton { Content = text, Margin = new Thickness(-12, 0, 0, 0) };
        link.Click += (_, _) => onClick();
        return link;
    }

    public static HyperlinkButton FileLink(string text, string path) => Link(text, () => TrayApp.OpenFile(path));

    public static string DayLabel(DateTime local)
    {
        DateTime today = DateTime.Today;
        if (local.Date == today) return "Today";
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        return local.ToString(local.Year == today.Year ? "dddd d MMMM" : "dddd d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Uses the icon embedded in Drowsy.exe for a window's title bar and taskbar button.</summary>
    public static unsafe void SetAppIcon(Microsoft.UI.Windowing.AppWindow window)
    {
        if (Environment.ProcessPath is not { } exe) return;
        nint icon = 0;
        Interop.Native.ExtractIconExW(exe, 0, &icon, null, 1);
        if (icon != 0) window.SetIcon(Microsoft.UI.Win32Interop.GetIconIdFromIcon(icon)); // the window keeps the handle
    }

    public static Brush Brush(string key, Windows.UI.Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush b ? b : new SolidColorBrush(fallback);
}
