namespace Drowsy.Activity;

internal enum Signal
{
    Input,
    Audio,
    Microphone,
    Controller,
    DisplayRequest,
    Process,
}

/// <summary>A signal's current state, for the tooltip, tray menu and settings window.</summary>
internal readonly record struct SignalState(Signal Signal, bool Enabled, bool Active, long? AgoMs, string? Detail);

/// <summary>
/// Tracks the last time each signal saw activity. The PC is idle for the time since the most
/// recent activity across all enabled signals (or since the last reset: startup, resume, pause end).
/// </summary>
internal sealed class ActivityMonitor
{
    /// <summary>A signal seen within this window counts as "keeping the PC awake right now".</summary>
    public const long ActiveWindowMs = 15_000;

    private static readonly Signal[] All = Enum.GetValues<Signal>();
    private const long LogAfterGapMs = 60_000;
    private const long Never = long.MinValue;

    private readonly long[] _last = new long[All.Length];
    private readonly string?[] _detail = new string?[All.Length];
    private Config _config;
    private long _baseline;
    private string _baselineReason = "startup";

    public ActivityMonitor(Config config)
    {
        _config = config;
        Array.Fill(_last, Never);
        ResetAll("startup");
    }

    public static long Now => Environment.TickCount64;

    public void UpdateConfig(Config config) => _config = config;

    /// <summary>Why the countdown last restarted ("startup", "resumed from sleep", ...).</summary>
    public string BaselineReason => _baselineReason;

    /// <summary>Restart the idle countdown (startup, resume from sleep, end of a pause).</summary>
    public void ResetAll(string reason)
    {
        _baseline = Now;
        _baselineReason = reason;
    }

    public void Mark(Signal signal, string? detail)
    {
        long now = Now;
        int i = (int)signal;
        long last = _last[i];
        if (last == Never || now - last >= LogAfterGapMs)
        {
            // Logged so phantom activity ("who touched the PC?") can be traced to a signal/device.
            string quiet = last == Never ? "first seen" : $"after {Format.Duration(now - last)} quiet";
            Log.Info($"Activity: {Format.Name(signal)} {quiet} — {detail ?? "-"}");
        }
        _last[i] = now;
        _detail[i] = detail;
    }

    public bool IsEnabled(Signal signal) => signal switch
    {
        Signal.Audio => _config.AudioIsActivity,
        Signal.Microphone => _config.MicrophoneIsActivity,
        Signal.Controller => _config.ControllerIsActivity,
        Signal.DisplayRequest => _config.HonorDisplayRequests,
        Signal.Process => _config.BlockingProcesses.Count > 0,
        _ => true,
    };

    /// <summary>Milliseconds since the most recent activity (or reset).</summary>
    public long IdleMs
    {
        get
        {
            long latest = _baseline;
            foreach (Signal s in All)
            {
                if (IsEnabled(s)) latest = Math.Max(latest, _last[(int)s]);
            }
            return Now - latest;
        }
    }

    public IEnumerable<SignalState> States()
    {
        long now = Now;
        foreach (Signal s in All)
        {
            int i = (int)s;
            bool enabled = IsEnabled(s);
            long? ago = _last[i] == Never ? null : now - _last[i];
            yield return new SignalState(s, enabled, enabled && ago < ActiveWindowMs, ago, _detail[i]);
        }
    }

    /// <summary>Signals that saw activity within <see cref="ActiveWindowMs"/>.</summary>
    public List<SignalState> KeepingAwake() => States().Where(s => s.Active).ToList();

    /// <summary>True during the post-reset grace period when no signal is active.</summary>
    public bool InGracePeriod(out string reason)
    {
        reason = _baselineReason;
        return Now - _baseline < ActiveWindowMs;
    }
}

internal static class Format
{
    public static string Duration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{t.Seconds}s";
    }

    public static string Name(Signal s) => s switch
    {
        Signal.Input => "Keyboard/mouse",
        Signal.Audio => "Audio playing",
        Signal.Microphone => "Microphone in use",
        Signal.Controller => "Controller",
        Signal.DisplayRequest => "App keeping display on",
        Signal.Process => "Blocking program",
        _ => s.ToString(),
    };

    /// <summary>Short, friendly detail: strips raw-input device paths down to VID/PID.</summary>
    public static string? FriendlyDetail(Signal s, string? detail)
    {
        if (detail is null || s != Signal.Input) return detail;
        // "mouse: \\?\HID#VID_1532&PID_00C1&MI_00#..." -> "mouse (VID_1532 PID_00C1)"
        int colon = detail.IndexOf(':');
        string kind = colon > 0 ? detail[..colon] : "input";
        int vid = detail.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        int pid = detail.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (vid >= 0 && pid >= 0 && vid + 8 <= detail.Length && pid + 8 <= detail.Length)
        {
            return $"{kind} ({detail.Substring(vid, 8)} {detail.Substring(pid, 8)})";
        }
        return detail.Contains("injected", StringComparison.OrdinalIgnoreCase) ? $"{kind} (injected)" : kind;
    }

    /// <summary>Plain-English reason a signal is keeping the PC awake.</summary>
    public static string Reason(Signal s, string? detail) => s switch
    {
        Signal.Input => "Keyboard or mouse in use",
        Signal.Audio => detail is { Length: > 0 } app ? $"{app} is playing audio" : "Audio is playing",
        Signal.Microphone => detail is { Length: > 0 } mic ? $"{mic} is using the microphone" : "An app is using the microphone",
        Signal.Controller => "Game controller in use",
        Signal.DisplayRequest => "An app is keeping the screen on",
        Signal.Process => detail is { Length: > 0 } p ? $"{p} is running" : "A blocking program is running",
        _ => s.ToString(),
    };

    /// <summary>Countdown text: "17 min" above two minutes, otherwise "1m 40s".</summary>
    public static string Countdown(long ms) =>
        ms >= 120_000 ? $"{(long)Math.Ceiling(ms / 60_000.0)} min" : Duration(ms);

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
