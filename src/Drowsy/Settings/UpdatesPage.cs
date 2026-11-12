using Drowsy.Update;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Drowsy.Settings.Ui;

namespace Drowsy.Settings;

/// <summary>Current version, "check now", and installing a newer release from GitHub.</summary>
internal sealed class UpdatesPage
{
    private readonly Action _exitForRestart;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _checkNow = new() { Content = "Check now" };
    private readonly Border _available;
    private readonly TextBlock _availableTitle = new() { FontSize = 20, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly Button _install = new() { Content = "Install and restart" };
    private readonly ProgressBar _progress = new() { Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, Title = "Update failed", IsClosable = true };
    private AvailableUpdate? _update;
    private bool _checked;
    private bool _busy;

    public UpdatesPage(ToggleSwitch autoCheck, Action exitForRestart)
    {
        _exitForRestart = exitForRestart;
        _checkNow.Click += async (_, _) => await CheckAsync();
        _install.Click += async (_, _) => await InstallAsync();

        var notesLink = Link("Full release notes on GitHub", () => OpenUrl(_update?.PageUrl));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        actions.Children.Add(_install);
        actions.Children.Add(notesLink);
        _available = Card(10, _availableTitle, _notes, _progress, actions);
        _available.Visibility = Visibility.Collapsed;

        var versionTitle = new TextBlock { Text = $"Drowsy {Updater.CurrentVersion}", FontWeight = FontWeights.SemiBold };
        var location = new TextBlock
        {
            Text = Updater.AppDir.TrimEnd('\\'),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextFillColorTertiaryBrush", Colors.Gray),
        };
        _status.Foreground = Brush("TextFillColorSecondaryBrush", Colors.Gray);
        var versionText = new StackPanel { Spacing = 2, Children = { versionTitle, _status, location } };
        var versionRow = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 8, 0, 8) };
        versionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        versionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _checkNow.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_checkNow, 1);
        versionRow.Children.Add(versionText);
        versionRow.Children.Add(_checkNow);

        View = Page("Updates",
            _error,
            _available,
            Card(0, versionRow, Divider(), Row("Check for updates automatically", autoCheck)),
            Note($"Drowsy checks GitHub ({Updater.Repository}) once a day and tells you when there's a new version. " +
                 "Nothing is installed without you choosing to."));

        if (!Updater.Enabled)
        {
            _status.Text = "This is a development build, so it doesn't update itself. Releases from GitHub do.";
            _checkNow.IsEnabled = false;
        }
    }

    public UIElement View { get; }

    /// <summary>Checks once per window, the first time the page is shown.</summary>
    public void OnShown()
    {
        if (!_checked && Updater.Enabled) _ = CheckAsync();
    }

    private async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        _checked = true;
        _checkNow.IsEnabled = false;
        _status.Text = "Checking for updates…";
        try
        {
            _update = await Updater.CheckAsync();
            _status.Text = _update is null
                ? $"You're up to date. Checked at {DateTime.Now:HH:mm}."
                : $"A new version is available. Checked at {DateTime.Now:HH:mm}.";
            ShowAvailable();
        }
        catch (Exception ex)
        {
            _status.Text = $"Couldn't check for updates: {ex.Message}";
        }
        finally
        {
            _busy = false;
            _checkNow.IsEnabled = true;
        }
    }

    private void ShowAvailable()
    {
        _available.Visibility = _update is null ? Visibility.Collapsed : Visibility.Visible;
        if (_update is null) return;
        _availableTitle.Text = $"Drowsy {_update.Version} is available";
        string notes = TidyNotes(_update.Notes);
        _notes.Text = notes;
        _notes.Visibility = notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task InstallAsync()
    {
        if (_update is not { } update || _busy) return;
        _busy = true;
        _install.IsEnabled = _checkNow.IsEnabled = false;
        _error.IsOpen = false;
        _progress.Value = 0;
        _progress.Visibility = Visibility.Visible;
        _install.Content = "Downloading…";
        try
        {
            string staged = await Updater.DownloadAsync(update, new Progress<double>(p => _progress.Value = p));
            _install.Content = "Restarting…";
            Updater.InstallAndRestart(staged, update);
            _exitForRestart();
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            _error.Message = ex.Message;
            _error.IsOpen = true;
            _progress.Visibility = Visibility.Collapsed;
            _install.Content = "Install and restart";
            _install.IsEnabled = _checkNow.IsEnabled = true;
            _busy = false;
        }
    }

    /// <summary>GitHub's generated notes are Markdown; show them as plain text.</summary>
    private static string TidyNotes(string markdown)
    {
        var lines = markdown.Replace("\r", "").Split('\n')
            .Select(l => l.TrimStart('#', ' ').Replace("**", "").TrimEnd())
            .Select(l => l.StartsWith("* ", StringComparison.Ordinal) || l.StartsWith("- ", StringComparison.Ordinal) ? "•  " + l[2..] : l);
        string text = string.Join('\n', lines).Trim();
        return text.Length > 1500 ? text[..1500].TrimEnd() + "…" : text;
    }

    private static void OpenUrl(string? url)
    {
        if (url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {url}", ex);
        }
    }
}
