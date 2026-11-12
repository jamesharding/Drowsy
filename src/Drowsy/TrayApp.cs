using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Drowsy.Activity;
using Drowsy.Interop;

namespace Drowsy;

/// <summary>
/// Hidden top-level window (needed for raw input, power broadcasts and the tray icon),
/// a 1-second timer that samples activity, and the sleep decision.
/// </summary>
internal sealed unsafe class TrayApp : IDisposable
{
    public const string WindowClass = "Drowsy.HiddenWindow";
    private const uint WM_TRAY = Native.WM_APP + 1;
    private const nuint TickTimerId = 1;
    private const nuint AudioTimerId = 2;
    private const int SlowPollEveryTicks = 5;
    private const long SleepRetryBackoffMs = 5 * 60_000;

    private enum Cmd : uint
    {
        Pause30 = 100, Pause60, Pause180, PauseIndefinite, Resume, ToggleEnabled,
        SleepNow, Settings, OpenLog, ToggleStartup, Exit, InstallUpdate,
    }

    private static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(24);
    private static readonly TimeSpan FirstUpdateCheckAfter = TimeSpan.FromMinutes(2);

    private static TrayApp? s_instance;

    private readonly ConfigStore _configStore;
    private readonly ActivityMonitor _monitor;
    private readonly RawInputSource _input = new();
    private readonly AudioSource _audio = new();
    private readonly ControllerSource _controller = new();
    private readonly SleepHistory _history = SleepHistory.Load();
    private readonly uint _taskbarCreatedMsg;

    private nint _hwnd;
    private nint _icon;
    private string _tooltip = "";
    private long _tick;
    private long _pausedUntil;        // 0 = not paused; long.MaxValue = until resumed
    private bool _warningShown;
    private long _sleepBlockedUntil;  // back-off after a failed sleep attempt
    private readonly string? _welcome;
    private DateTime _nextUpdateCheckUtc = DateTime.UtcNow + FirstUpdateCheckAfter;
    private volatile Update.AvailableUpdate? _update; // set from a background check
    private Version? _announcedUpdate;
    private bool _balloonOpensUpdates;

    public TrayApp(ConfigStore configStore, string? welcome = null)
    {
        _configStore = configStore;
        _welcome = welcome;
        _monitor = new ActivityMonitor(configStore.Current);
        _taskbarCreatedMsg = Native.RegisterWindowMessageW("TaskbarCreated");
        s_instance = this;
        if (_history.Imported) _history.Save(); // keep what the log knew before it rolls over
    }

    private Config Config => _configStore.Current;

    public int Run()
    {
        CreateWindow();
        RawInputSource.Register(_hwnd);
        LoadIcon();
        AddTrayIcon();
        Native.SetTimer(_hwnd, TickTimerId, 1000, 0);
        Native.SetTimer(_hwnd, AudioTimerId, AudioSource.SampleIntervalMs, 0);

        Log.Info($"Started. Sleep after {Config.IdleMinutes} min idle; audio={Config.AudioIsActivity} " +
                 $"controller={Config.ControllerIsActivity} " +
                 $"displayRequests={Config.HonorDisplayRequests} blockingProcesses=[{string.Join(", ", Config.BlockingProcesses)}] " +
                 $"version={Update.Updater.CurrentVersion}");
        if (_welcome is not null) ShowBalloon("Drowsy", _welcome);

        MSG msg;
        while (Native.GetMessageW(&msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(&msg);
            Native.DispatchMessageW(&msg);
        }
        return 0;
    }

    // ---- Window ----------------------------------------------------------------------------

    private void CreateWindow()
    {
        nint instance = Native.GetModuleHandleW(0);
        fixed (char* className = WindowClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                lpszClassName = className,
            };
            if (Native.RegisterClassExW(&wc) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassEx failed");
            }
        }

        // A real (never shown) top-level window: message-only windows don't receive broadcasts
        // such as WM_POWERBROADCAST and TaskbarCreated.
        const uint WS_EX_TOOLWINDOW = 0x00000080;
        _hwnd = Native.CreateWindowExW(WS_EX_TOOLWINDOW, WindowClass, "Drowsy", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        if (_hwnd == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowEx failed");
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (s_instance is { } app && app.HandleMessage(msg, wParam, lParam) is { } handled)
            {
                return handled;
            }
        }
        catch (Exception ex)
        {
            // Exceptions must never unwind through native frames.
            Log.Error($"Unhandled error handling message 0x{msg:X4}", ex);
        }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private nint? HandleMessage(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Native.WM_INPUT:
                OnRawInput(lParam);
                return null; // DefWindowProc must still run to free the raw input buffer

            case Native.WM_TIMER when (nuint)wParam == TickTimerId:
                Tick();
                return 0;

            case Native.WM_TIMER when (nuint)wParam == AudioTimerId:
                if (Config.AudioIsActivity || Config.MicrophoneIsActivity) _audio.Sample(Config);
                return 0;

            case WM_TRAY:
                OnTrayMessage((uint)(lParam & 0xFFFF));
                return 0;

            case Native.WM_POWERBROADCAST:
                OnPowerBroadcast(wParam);
                return 1;

            case Native.WM_ENDSESSION when wParam != 0:
                RemoveTrayIcon();
                return 0;

            case Native.WM_DESTROY:
                RemoveTrayIcon();
                Native.PostQuitMessage(0);
                return 0;

            default:
                if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
                {
                    AddTrayIcon(); // Explorer restarted
                    return 0;
                }
                return null;
        }
    }

    // ---- Activity ----------------------------------------------------------------------------

    private void OnRawInput(nint hRawInput)
    {
        if (!_input.TryRead(hRawInput, out nint device, out string kind)) return;
        if (device == 0 && Config.IgnoreInjectedInput) return;

        string name = _input.DeviceName(device);
        if (Config.IgnoredInputDevices.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase))) return;

        _monitor.Mark(Signal.Input, $"{kind}: {name}");
    }

    private void Tick()
    {
        _tick++;
        if (_configStore.ReloadIfChanged()) _monitor.UpdateConfig(Config);

        if (Config.ControllerIsActivity && _controller.Poll() is { } pad)
        {
            _monitor.Mark(Signal.Controller, $"controller {pad + 1}");
        }

        if (Config.AudioIsActivity && _audio.PlayingApp(Config) is { } app)
        {
            _monitor.Mark(Signal.Audio, app);
        }

        if (Config.MicrophoneIsActivity && _audio.MicrophoneApp() is { } micApp)
        {
            _monitor.Mark(Signal.Microphone, micApp);
        }

        if (_tick % SlowPollEveryTicks == 0)
        {
            if (Config.HonorDisplayRequests && SystemStateSource.DisplayRequested())
                _monitor.Mark(Signal.DisplayRequest, "an app asked Windows to keep the display on");

            if (Config.BlockingProcesses.Count > 0 && SystemStateSource.RunningBlockingProcess(Config.BlockingProcesses) is { } proc)
                _monitor.Mark(Signal.Process, proc);
        }

        EvaluateSleep();
        CheckForUpdates();

        EngineStatus status = BuildStatus();
        UpdateTooltip(status);
        if (Program.SettingsWindowIsOpen()) StatusFile.Write(status);
    }

    private bool IsPaused => _pausedUntil != 0 && ActivityMonitor.Now < _pausedUntil;

    /// <summary>Once a day, look for a newer release in the background; announce each new version once.</summary>
    private void CheckForUpdates()
    {
        if (_update is { } update && update.Version != _announcedUpdate)
        {
            _announcedUpdate = update.Version;
            Log.Info($"Update available: {update.Tag}");
            ShowBalloon($"Drowsy {update.Version} is available", "Click to see what's new and install it.");
            _balloonOpensUpdates = true;
        }

        if (!Update.Updater.Enabled || !Config.CheckForUpdates || DateTime.UtcNow < _nextUpdateCheckUtc) return;
        _nextUpdateCheckUtc = DateTime.UtcNow + UpdateCheckEvery;
        Update.Updater.CheckInBackground(found => _update = found);
    }

    private void EvaluateSleep()
    {
        if (_pausedUntil != 0 && !IsPaused)
        {
            _pausedUntil = 0;
            _monitor.ResetAll("pause ended");
            Log.Info("Pause ended");
        }

        long now = ActivityMonitor.Now;
        long idle = _monitor.IdleMs;
        long limit = Config.IdleMinutes * 60_000L;
        long warnAt = limit - Config.WarningSeconds * 1000L;

        if (!Config.Enabled || IsPaused || now < _sleepBlockedUntil || idle < warnAt)
        {
            _warningShown = false;
            return;
        }

        if (!_warningShown && Config.WarningSeconds > 0 && idle < limit)
        {
            _warningShown = true;
            ShowBalloon("Going to sleep soon",
                $"Sleeping in {Format.Duration(limit - idle)}. Move the mouse or press a key to stay awake.");
        }

        if (idle >= limit)
        {
            _warningShown = false;
            GoToSleep(SleepTrigger.Idle, $"idle for {Format.Duration(idle)}");
        }
    }

    private void GoToSleep(string trigger, string reason)
    {
        Log.Info($"Sleeping: {reason}");
        RecordSleep(trigger);
        if (Power.TrySleep(out int error))
        {
            // Returns after resume on most systems; the resume broadcast also resets timers.
            _monitor.ResetAll("resumed");
        }
        else
        {
            _history.Refused(error);
            _history.Save();
            _sleepBlockedUntil = ActivityMonitor.Now + SleepRetryBackoffMs;
            Log.Error($"SetSuspendState failed (Win32 error {error}); retrying in {Format.Duration(SleepRetryBackoffMs)}");
            ShowBalloon("Couldn't sleep", $"Windows refused the sleep request (error {error}). See the log for details.");
        }
    }

    /// <summary>Saved before the sleep request, so it's on disk even if the PC never comes back.</summary>
    private void RecordSleep(string trigger)
    {
        long idle = _monitor.IdleMs;
        DateTime now = DateTime.UtcNow;
        var e = new SleepEvent
        {
            SleptUtc = now,
            Trigger = trigger,
            IdleSeconds = idle / 1000,
            IdleSinceUtc = now - TimeSpan.FromMilliseconds(idle),
        };
        // The countdown started either at the last activity or at a reset (startup, resume, ...).
        if (LatestActivity() is { AgoMs: { } ago } latest && ago <= idle + 1500)
            e.LastActivity = Format.Reason(latest.Signal, latest.Detail);
        else
            e.IdleSinceReason = _monitor.BaselineReason;
        _history.Begin(e);
        _history.Save();
    }

    private void OnPowerBroadcast(nint eventType)
    {
        switch (eventType)
        {
            case Native.PBT_APMSUSPEND:
                Log.Info("System suspending");
                if (_history.Suspending(DateTime.UtcNow)) _history.Save();
                break;
            case Native.PBT_APMRESUMEAUTOMATIC:
            case Native.PBT_APMRESUMESUSPEND:
                if (_history.Woke(DateTime.UtcNow)) _history.Save();
                _monitor.ResetAll("resumed from sleep");
                _warningShown = false;
                Log.Info(eventType == Native.PBT_APMRESUMESUSPEND ? "Resumed (user)" : "Resumed");
                break;
        }
    }

    // ---- Status ------------------------------------------------------------------------------

    /// <summary>Single source of truth for the tooltip, tray menu and settings window.</summary>
    private EngineStatus BuildStatus()
    {
        long idle = _monitor.IdleMs;
        long limit = Config.IdleMinutes * 60_000L;
        List<SignalState> awake = _monitor.KeepingAwake();
        var status = new EngineStatus
        {
            UpdatedUtc = DateTime.UtcNow,
            IdleSeconds = idle / 1000,
            LimitSeconds = limit / 1000,
            Signals = _monitor.States().Select(StatusFile.ToStatus).ToList(),
        };

        status.Reasons = awake.Select(s => Format.Reason(s.Signal, s.Detail)).ToList();

        if (LatestActivity() is { AgoMs: { } ago } l)
        {
            status.LastActivity = $"{Format.Reason(l.Signal, l.Detail)}, {Format.Duration(ago)} ago";
        }

        if (!Config.Enabled)
        {
            status.State = "Disabled";
            status.Title = "Off";
            status.Headline = "Drowsy won't put the PC to sleep";
        }
        else if (IsPaused)
        {
            status.State = "Paused";
            status.Title = "Paused";
            status.Headline = _pausedUntil == long.MaxValue
                ? "Staying awake until you resume"
                : $"Staying awake for another {Format.Countdown(_pausedUntil - ActivityMonitor.Now)}";
        }
        else if (awake.Count > 0)
        {
            status.State = "Awake";
            status.Title = "Staying awake";
            status.Headline = string.Join(" · ", status.Reasons);
        }
        else
        {
            long sleepIn = Math.Max(0, limit - idle);
            status.SleepInSeconds = sleepIn / 1000;
            status.Title = $"Sleeping in {Format.Countdown(sleepIn)}";
            if (_monitor.InGracePeriod(out string reason))
            {
                status.State = "Grace";
                status.Headline = $"Countdown restarted ({reason})";
            }
            else
            {
                status.State = sleepIn <= Config.WarningSeconds * 1000L ? "SleepingSoon" : "Idle";
                status.Headline = "Nothing is keeping the PC awake";
            }
        }
        return status;
    }

    /// <summary>The most recently seen enabled signal, if any.</summary>
    private SignalState? LatestActivity()
    {
        SignalState? latest = null;
        foreach (SignalState s in _monitor.States())
        {
            if (s.Enabled && s.AgoMs is { } a && (latest is null || a < latest.Value.AgoMs)) latest = s;
        }
        return latest;
    }

    private void UpdateTooltip(EngineStatus status)
    {
        // Two short lines; Windows caps tooltips at 127 characters.
        string text = Format.Truncate($"Drowsy: {status.Title}\n{status.Headline}", 127);
        if (text == _tooltip) return;
        _tooltip = text;

        NOTIFYICONDATAW nid = NewIconData();
        nid.uFlags = Native.NIF_TIP | Native.NIF_SHOWTIP;
        CopyString(text, nid.szTip, 128);
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, &nid);
    }

    // ---- Tray icon -------------------------------------------------------------------------

    private void LoadIcon()
    {
        nint small = 0;
        if (Environment.ProcessPath is { } exe) Native.ExtractIconExW(exe, 0, null, &small, 1);
        _icon = small;
    }

    private NOTIFYICONDATAW NewIconData() => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _hwnd,
        uID = 1,
    };

    private void AddTrayIcon()
    {
        NOTIFYICONDATAW nid = NewIconData();
        nid.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
        nid.uCallbackMessage = WM_TRAY;
        nid.hIcon = _icon;
        CopyString(_tooltip.Length > 0 ? _tooltip : "Drowsy", nid.szTip, 128);
        Native.Shell_NotifyIconW(Native.NIM_ADD, &nid);

        nid.uVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIconW(Native.NIM_SETVERSION, &nid);
    }

    private void RemoveTrayIcon()
    {
        NOTIFYICONDATAW nid = NewIconData();
        Native.Shell_NotifyIconW(Native.NIM_DELETE, &nid);
    }

    private void ShowBalloon(string title, string text)
    {
        _balloonOpensUpdates = false;
        NOTIFYICONDATAW nid = NewIconData();
        nid.uFlags = Native.NIF_INFO;
        nid.dwInfoFlags = Native.NIIF_INFO | Native.NIIF_NOSOUND;
        CopyString(title, nid.szInfoTitle, 64);
        CopyString(text, nid.szInfo, 256);
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, &nid);
    }

    private static void CopyString(string s, char* dest, int capacity)
    {
        int n = Math.Min(s.Length, capacity - 1);
        s.AsSpan(0, n).CopyTo(new Span<char>(dest, capacity));
        dest[n] = '\0';
    }

    // ---- Menu --------------------------------------------------------------------------------

    private void OnTrayMessage(uint evt)
    {
        if (evt is Native.WM_CONTEXTMENU or Native.WM_RBUTTONUP)
        {
            ShowMenu();
        }
        else if (evt is Native.NIN_SELECT or Native.NIN_KEYSELECT or Native.WM_LBUTTONUP)
        {
            Program.LaunchSettings(); // left-click opens the settings/status window
        }
        else if (evt == Native.NIN_BALLOONUSERCLICK && _balloonOpensUpdates)
        {
            Program.LaunchSettings("--updates");
        }
    }

    private void ShowMenu()
    {
        nint menu = Native.CreatePopupMenu();
        try
        {
            EngineStatus status = BuildStatus();
            Native.AppendMenuW(menu, Native.MF_STRING | Native.MF_GRAYED, 0, status.Title);
            if (status.Reasons.Count > 0)
            {
                foreach (string reason in status.Reasons)
                    Native.AppendMenuW(menu, Native.MF_STRING | Native.MF_GRAYED, 0, "   ● " + Format.Truncate(reason, 80));
            }
            else
            {
                Native.AppendMenuW(menu, Native.MF_STRING | Native.MF_GRAYED, 0, "   " + Format.Truncate(status.Headline, 80));
            }
            Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
            Native.AppendMenuW(menu, Native.MF_STRING | (Config.Enabled ? Native.MF_CHECKED : 0), (nuint)Cmd.ToggleEnabled, "Enabled");

            if (!Config.Enabled)
            {
                // nothing to pause
            }
            else if (IsPaused)
            {
                Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Cmd.Resume, "Resume");
            }
            else
            {
                Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Cmd.Pause30, "Stay awake for 30 minutes");
                Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Cmd.Pause60, "Stay awake for 1 hour");
                Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Cmd.Pause180, "Stay awake for 3 hours");
                Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Cmd.PauseIndefinite, "Stay awake until I resume");
            }
            Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Cmd.SleepNow, "Sleep now");
            Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
            Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Cmd.Settings, "Status && settings…");
            if (_update is { } update)
                Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Cmd.InstallUpdate, $"Update to {update.Version}…");
            Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Cmd.OpenLog, "Open log");
            Native.AppendMenuW(menu, Native.MF_STRING | (Startup.IsEnabled ? Native.MF_CHECKED : 0), (nuint)Cmd.ToggleStartup, "Start with Windows");
            Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
            Native.AppendMenuW(menu, Native.MF_STRING, (nuint)Cmd.Exit, "Exit");

            POINT pt;
            Native.GetCursorPos(&pt);
            Native.SetForegroundWindow(_hwnd); // required so the menu closes when clicking elsewhere
            int chosen = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY,
                pt.X, pt.Y, _hwnd, 0);
            Native.PostMessageW(_hwnd, Native.WM_NULL, 0, 0);

            if (chosen != 0) OnCommand((Cmd)chosen);
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    private void OnCommand(Cmd cmd)
    {
        switch (cmd)
        {
            case Cmd.Pause30: Pause(30); break;
            case Cmd.Pause60: Pause(60); break;
            case Cmd.Pause180: Pause(180); break;
            case Cmd.PauseIndefinite: Pause(null); break;
            case Cmd.Resume:
                _pausedUntil = 0;
                _monitor.ResetAll("resumed by you");
                Log.Info("Resumed by user");
                break;
            case Cmd.ToggleEnabled:
                Config.Enabled = !Config.Enabled;
                ConfigStore.Save(Config); // persists; the settings window sees it too
                _monitor.ResetAll(Config.Enabled ? "turned on" : "turned off");
                Log.Info(Config.Enabled ? "Enabled from tray menu" : "Disabled from tray menu");
                break;
            case Cmd.SleepNow: GoToSleep(SleepTrigger.Manual, "requested from tray menu"); break;
            case Cmd.Settings: Program.LaunchSettings(); break;
            case Cmd.InstallUpdate: Program.LaunchSettings("--updates"); break;
            case Cmd.OpenLog: OpenFile(AppPaths.LogFile); break;
            case Cmd.ToggleStartup:
                try
                {
                    Startup.Set(!Startup.IsEnabled);
                }
                catch (InvalidOperationException ex)
                {
                    ShowBalloon("Can't start with Windows from here", ex.Message);
                }
                break;
            case Cmd.Exit:
                Log.Info("Exit requested");
                Native.DestroyWindow(_hwnd);
                break;
        }
        UpdateTooltip(BuildStatus());
    }

    private void Pause(int? minutes)
    {
        _pausedUntil = minutes is { } m ? ActivityMonitor.Now + m * 60_000L : long.MaxValue;
        _warningShown = false;
        Log.Info(minutes is { } mm ? $"Paused for {mm} min" : "Paused until resumed");
    }

    public static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
        }
    }

    public void Dispose()
    {
        _audio.Dispose();
        if (_icon != 0) Native.DestroyIcon(_icon);
        s_instance = null;
    }
}
