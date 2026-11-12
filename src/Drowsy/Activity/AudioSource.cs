using System.Diagnostics;
using Drowsy.Interop;

namespace Drowsy.Activity;

/// <summary>Live audio state of one app, for the Audio activity window.</summary>
/// <remarks><see cref="Triggering"/> is about playback; <see cref="MicActive"/> means the app has the microphone open.</remarks>
internal readonly record struct AppAudio(string App, float Peak, bool Ignored, long AudibleMs, bool Triggering, bool MicActive);

/// <summary>
/// Detects sustained, audible playback per application.
///
/// Why per-app sessions rather than whole playback devices:
///  - Mixers such as Elgato Wave Link keep device streams open permanently (which is what stops
///    Windows sleeping) and their output sessions repeat everything the apps play, plus mic
///    monitoring / room noise. Measuring the apps themselves (and ignoring the mixer) avoids that.
///  - The status can name the app ("Plexamp is playing audio").
///
/// Why "sustained": a notification ping now and then shouldn't keep the PC awake all night, so an
/// app only counts once it has been audible for <see cref="Config.AudioMinSeconds"/> within the last
/// <see cref="WindowMs"/>.
/// </summary>
internal sealed class AudioSource : IDisposable
{
    public const int SampleIntervalMs = 250;
    public const long WindowMs = 10_000;
    private const long SessionRefreshMs = 3_000;

    private sealed class Session(string app, bool ignored, bool capture, IAudioSessionControl2 control, IAudioMeterInformation meter, object com)
    {
        public string App { get; } = app;
        public bool Ignored { get; } = ignored;
        public bool Capture { get; } = capture; // microphone (recording) session rather than playback
        public IAudioSessionControl2 Control { get; } = control;
        public IAudioMeterInformation Meter { get; } = meter;
        public object Com { get; } = com;
        public float LastPeak { get; set; }
        public bool Active { get; set; }
    }

    private readonly List<Session> _sessions = [];
    private readonly Dictionary<string, Queue<long>> _audible = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _processNames = [];
    private readonly HashSet<string> _thisSample = new(StringComparer.OrdinalIgnoreCase);
    private IMMDeviceEnumerator? _enumerator;
    private long _nextRefresh;

    /// <summary>Force the session list to be rebuilt on the next sample (e.g. ignore list changed).</summary>
    public void Invalidate() => _nextRefresh = 0;

    /// <summary>Called every <see cref="SampleIntervalMs"/>: reads every session's peak and records audible apps.</summary>
    public void Sample(Config config)
    {
        long now = ActivityMonitor.Now;
        ReadPeaks(config);

        _thisSample.Clear();
        foreach (Session s in _sessions)
        {
            if (!s.Capture && !s.Ignored && s.LastPeak > config.AudioPeakThreshold) _thisSample.Add(s.App);
        }

        foreach (string app in _thisSample)
        {
            if (!_audible.TryGetValue(app, out Queue<long>? q)) _audible[app] = q = new Queue<long>();
            q.Enqueue(now);
        }
        foreach (Queue<long> q in _audible.Values)
        {
            while (q.Count > 0 && now - q.Peek() > WindowMs) q.Dequeue();
        }
    }

    /// <summary>Refreshes the peak meters only (no bookkeeping) — for smooth UI meters between samples.</summary>
    public void ReadPeaks(Config config)
    {
        if (ActivityMonitor.Now >= _nextRefresh) Refresh(config);
        foreach (Session s in _sessions)
        {
            try
            {
                if (s.Capture)
                {
                    // For the mic, "in use" means the app has a running capture stream — level
                    // doesn't matter (you can be silent on a call).
                    s.Active = s.Control.GetState(out int state) == 0 && state == CoreAudio.AudioSessionStateActive;
                }
                else
                {
                    s.LastPeak = s.Meter.GetPeakValue();
                }
            }
            catch
            {
                s.LastPeak = 0;
                s.Active = false;
                _nextRefresh = 0; // session/device went away; rebuild next time
            }
        }
    }

    /// <summary>The app with the most sustained audible output in the last 10 s, or null.</summary>
    public string? PlayingApp(Config config)
    {
        string? best = null;
        long bestMs = 0;
        foreach (AppAudio a in Snapshot(config))
        {
            if (a.Triggering && a.AudibleMs > bestMs)
            {
                best = a.App;
                bestMs = a.AudibleMs;
            }
        }
        return best;
    }

    /// <summary>An app (not ignored) that currently has the microphone open, or null.</summary>
    public string? MicrophoneApp() =>
        _sessions.FirstOrDefault(s => s.Capture && s.Active && !s.Ignored)?.App;

    /// <summary>Every app with an audio session: level, ignored flag, whether playback is keeping the PC awake, and mic use.</summary>
    public List<AppAudio> Snapshot(Config config)
    {
        long needMs = Math.Max(0, config.AudioMinSeconds) * 1000L;
        return _sessions
            .GroupBy(s => s.App, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                long audibleMs = _audible.TryGetValue(g.Key, out Queue<long>? q) ? q.Count * (long)SampleIntervalMs : 0;
                bool ignored = g.Any(s => s.Ignored);
                float peak = g.Where(s => !s.Capture).Select(s => s.LastPeak).DefaultIfEmpty(0).Max();
                bool mic = g.Any(s => s.Capture && s.Active);
                return new AppAudio(g.Key, peak, ignored, audibleMs, !ignored && audibleMs > 0 && audibleMs >= needMs, mic);
            })
            .ToList();
    }

    private void Refresh(Config config)
    {
        _nextRefresh = ActivityMonitor.Now + SessionRefreshMs;
        ReleaseSessions();
        try
        {
            _enumerator ??= CoreAudio.CreateEnumerator();
            foreach (int flow in new[] { CoreAudio.eRender, CoreAudio.eCapture })
            {
                IMMDeviceCollection devices = _enumerator.EnumAudioEndpoints(flow, CoreAudio.DEVICE_STATE_ACTIVE);
                uint deviceCount = devices.GetCount();
                for (uint d = 0; d < deviceCount; d++)
                {
                    IAudioSessionManager2 manager = CoreAudio.ActivateSessionManager(devices.Item(d));
                    try
                    {
                        IAudioSessionEnumerator sessions = manager.GetSessionEnumerator();
                        int count = sessions.GetCount();
                        for (int i = 0; i < count; i++) AddSession(sessions.GetSession(i), config, flow == CoreAudio.eCapture);
                    }
                    finally
                    {
                        CoreAudio.Release(manager);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audio: session enumeration failed", ex);
            CoreAudio.Release(_enumerator);
            _enumerator = null;
        }
    }

    private void AddSession(nint raw, Config config, bool capture)
    {
        object com = CoreAudio.WrapAndRelease(raw);
        try
        {
            var control = (IAudioSessionControl2)com;
            if (control.GetState(out int state) != 0 || state == CoreAudio.AudioSessionStateExpired)
            {
                CoreAudio.Release(com);
                return;
            }

            control.GetProcessId(out uint pid);
            string app = control.IsSystemSoundsSession() == 0 || pid == 0 ? "System sounds" : ProcessName(pid);
            bool ignored = config.IgnoredAudioApps.Any(x => app.Equals(x, StringComparison.OrdinalIgnoreCase));
            _sessions.Add(new Session(app, ignored, capture, control, (IAudioMeterInformation)com, com));
        }
        catch
        {
            CoreAudio.Release(com);
        }
    }

    /// <summary>Names of apps that currently have a playback session (for the settings window's picker).</summary>
    public static List<string> ListSessionApps()
    {
        using var scratch = new AudioSource();
        scratch.Refresh(new Config { IgnoredAudioApps = [] });
        return scratch._sessions.Select(s => s.App)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string ProcessName(uint pid)
    {
        if (_processNames.TryGetValue(pid, out string? name)) return name;
        try
        {
            using Process p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
        }
        catch
        {
            name = $"Process {pid}";
        }
        if (_processNames.Count > 512) _processNames.Clear();
        _processNames[pid] = name;
        return name;
    }

    private void ReleaseSessions()
    {
        foreach (Session s in _sessions) CoreAudio.Release(s.Com);
        _sessions.Clear();
    }

    public void Dispose()
    {
        ReleaseSessions();
        CoreAudio.Release(_enumerator);
        _enumerator = null;
    }
}
