using Microsoft.Win32;

namespace Drowsy;

/// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Drowsy";

    private static string Command => $"\"{AppPaths.EngineExe}\"";

    public static bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value &&
                   string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Null when "Start with Windows" can be turned on; otherwise why not.
    /// An unsigned exe in a temp or Downloads folder registering itself to run at sign-in is exactly
    /// what malware does, and Microsoft Defender quarantines it (Behavior:Win32/Persistence.A!ml).
    /// Those folders also get cleaned out, which would leave a dead startup entry.
    /// </summary>
    public static string? WhyCantEnable()
    {
        if (Environment.ProcessPath is null) return "Couldn't determine where Drowsy is running from.";
        return RunningFromTransientFolder
            ? "Drowsy is running from a temporary or Downloads folder. Close it and run it again to install it, or move " +
              @"the Drowsy folder somewhere permanent (for example %LOCALAPPDATA%\Programs\Drowsy), run it from there, then turn this on."
            : null;
    }

    /// <summary>True when running from a temp or Downloads folder (e.g. straight out of the zip).</summary>
    public static bool RunningFromTransientFolder
    {
        get
        {
            if (Environment.ProcessPath is not { } exe) return false;
            string dir = Path.GetDirectoryName(Path.GetFullPath(exe)) + Path.DirectorySeparatorChar;
            string[] transient =
            [
                Path.GetFullPath(Path.GetTempPath()),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") + Path.DirectorySeparatorChar,
            ];
            return transient.Any(folder => dir.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Turns startup on or off. Throws <see cref="InvalidOperationException"/> with a user-facing reason if it can't be turned on.</summary>
    public static void Set(bool enabled)
    {
        if (enabled && WhyCantEnable() is { } reason)
        {
            Log.Info($"Start with Windows refused: {reason}");
            throw new InvalidOperationException(reason);
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Info($"Start with Windows: {(enabled ? "on" : "off")}");
    }
}
