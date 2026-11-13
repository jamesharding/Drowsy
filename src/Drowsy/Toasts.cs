using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Drowsy;

/// <summary>
/// Windows notifications for the sleep warning and the "sleeping" record.
///
/// Tray balloons (<see cref="TrayApp"/>'s ShowBalloon) are shown as banners only and never kept in the
/// notification centre, so a missed warning left no trace. These use Windows' own notification API
/// (part of the OS, not the Windows App Runtime, so the engine still runs without the runtime).
/// An unpackaged app needs an identity for Windows to keep its notifications: it's registered under
/// HKCU\Software\Classes\AppUserModelId, which also gives them Drowsy's name and icon.
///
/// Each notification has a fixed tag, so a new one replaces the last: the centre holds at most one
/// warning and one "sleeping" entry from Drowsy.
/// </summary>
internal static class Toasts
{
    private const string AppId = "Drowsy.App";
    private const string Group = "sleep";
    public const string WarningTag = "warning";
    public const string SleepingTag = "sleeping";

    private static readonly string IconFile = Path.Combine(AppPaths.LogDir, "Drowsy.ico");
    private static bool _registered;

    /// <summary>Shows a notification; false if Windows refused it (the caller can fall back to a balloon).</summary>
    public static bool Show(string tag, string title, string text, bool silent)
    {
        try
        {
            Register();
            var xml = new XmlDocument();
            xml.LoadXml(
                "<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(text)}</text>" +
                "</binding></visual>" +
                (silent ? "<audio silent=\"true\"/>" : "") +
                "</toast>");
            var toast = new ToastNotification(xml) { Tag = tag, Group = Group };
            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't show the '{tag}' notification", ex);
            return false;
        }
    }

    /// <summary>Takes a notification off the screen and out of the notification centre.</summary>
    public static void Remove(string tag)
    {
        try
        {
            ToastNotificationManager.History.Remove(tag, Group, AppId);
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't remove the '{tag}' notification", ex);
        }
    }

    private static void Register()
    {
        if (_registered) return;

        using (Stream? icon = typeof(Toasts).Assembly.GetManifestResourceStream("Drowsy.ico"))
        {
            if (icon is not null && (!File.Exists(IconFile) || new FileInfo(IconFile).Length != icon.Length))
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                using var file = File.Create(IconFile);
                icon.CopyTo(file);
            }
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
        key.SetValue("DisplayName", "Drowsy");
        if (File.Exists(IconFile)) key.SetValue("IconUri", IconFile);
        _registered = true;
    }
}
