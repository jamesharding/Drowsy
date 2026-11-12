namespace Drowsy;

/// <summary>Tiny append-only file log with a single rollover at 1 MB.</summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(AppPaths.LogFile, AppPaths.LogFile + ".old", overwrite: true);
                }
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }
}

/// <summary>Reading the log back (settings window, history import).</summary>
internal static class LogFile
{
    /// <summary>All lines of a log file, readable while the engine is writing to it; empty if missing.</summary>
    public static string[] ReadLines(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Splits "2026-11-12 03:23:03 INFO  Sleeping: idle for 10m 00s" into its parts.</summary>
    public static bool TryParse(string line, out DateTime local, out bool error, out string message)
    {
        local = default;
        error = false;
        message = line;
        if (line.Length < 25 || !DateTime.TryParseExact(line.AsSpan(0, 19), "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out local))
        {
            return false;
        }
        error = line.AsSpan(20, 5).SequenceEqual("ERROR");
        message = line[25..].Trim();
        return true;
    }

    /// <summary>"mouse: \\?\HID#VID_1532&amp;PID_00C1&amp;MI_00#..." -> "mouse: VID_1532&amp;PID_00C1".</summary>
    public static string ShortenDevicePaths(string s)
    {
        int hid = s.IndexOf(@"\\?\", StringComparison.Ordinal);
        if (hid < 0) return s;
        int vid = s.IndexOf("VID_", hid, StringComparison.OrdinalIgnoreCase);
        return s[..hid] + (vid >= 0 && vid + 17 <= s.Length ? s.Substring(vid, 17) : "device");
    }
}
