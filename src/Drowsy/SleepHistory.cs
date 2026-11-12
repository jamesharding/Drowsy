using System.Text.Json;
using System.Text.Json.Serialization;

namespace Drowsy;

/// <summary>One time the PC went to sleep (or Drowsy tried to put it to sleep).</summary>
public sealed class SleepEvent
{
    public DateTime SleptUtc { get; set; }

    /// <summary>Null until the resume is seen (or if it never was, e.g. Drowsy was closed while asleep).</summary>
    public DateTime? WokeUtc { get; set; }

    /// <summary><see cref="SleepTrigger"/>: "Idle", "Manual" or "Other" (Start menu, lid, another app).</summary>
    public string Trigger { get; set; } = "";

    /// <summary>Idle time when Drowsy decided to sleep.</summary>
    public long? IdleSeconds { get; set; }

    /// <summary>When the idle countdown began: the last activity, or a reset such as startup or resume.</summary>
    public DateTime? IdleSinceUtc { get; set; }

    /// <summary>The last thing that counted as activity ("Keyboard or mouse in use"), if it started the countdown.</summary>
    public string? LastActivity { get; set; }

    /// <summary>Why the countdown restarted ("startup", "resumed from sleep") when no activity was seen since.</summary>
    public string? IdleSinceReason { get; set; }

    /// <summary>True when Windows refused the sleep request.</summary>
    public bool Failed { get; set; }

    public int? Error { get; set; }
}

internal static class SleepTrigger
{
    public const string Idle = "Idle";
    public const string Manual = "Manual";
    public const string Other = "Other";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<SleepEvent>))]
internal sealed partial class SleepHistoryJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Sleep history, kept in %LOCALAPPDATA%\Drowsy\sleep-history.json (separate from the log, which
/// rolls over). Written only by the engine; the settings window reads it.
/// </summary>
internal sealed class SleepHistory
{
    public static readonly string Path = System.IO.Path.Combine(AppPaths.LogDir, "sleep-history.json");
    private const int MaxEvents = 500;

    private SleepEvent? _pending; // the sleep we're waiting to see the resume for

    public List<SleepEvent> Events { get; private init; } = [];

    /// <summary>True when there was no history file and these events were rebuilt from the log.</summary>
    public bool Imported { get; private init; }

    public static SleepHistory Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                string json = File.ReadAllText(Path);
                return new SleepHistory { Events = JsonSerializer.Deserialize(json, SleepHistoryJsonContext.Default.ListSleepEvent) ?? [] };
            }
            return ImportFromLog();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {Path}", ex);
            return new SleepHistory();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Events, SleepHistoryJsonContext.Default.ListSleepEvent));
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save {Path}", ex);
        }
    }

    /// <summary>Drowsy is about to ask Windows to sleep.</summary>
    public void Begin(SleepEvent e)
    {
        Events.Add(e);
        if (Events.Count > MaxEvents) Events.RemoveRange(0, Events.Count - MaxEvents);
        _pending = e;
    }

    /// <summary>Windows refused the request started by <see cref="Begin"/>.</summary>
    public void Refused(int error)
    {
        if (_pending is null) return;
        _pending.Failed = true;
        _pending.Error = error;
        _pending = null;
    }

    /// <summary>
    /// The system is suspending. Records a sleep Drowsy didn't start; returns true if it added one.
    /// (After Drowsy's own request this broadcast often arrives late, after the resume.)
    /// </summary>
    public bool Suspending(DateTime utc)
    {
        if (_pending is not null) return false;
        Begin(new SleepEvent { SleptUtc = utc, Trigger = SleepTrigger.Other });
        return true;
    }

    /// <summary>The system resumed. Returns true if it completed a pending sleep.</summary>
    public bool Woke(DateTime utc)
    {
        if (_pending is null) return false;
        _pending.WokeUtc = utc;
        _pending = null;
        return true;
    }

    /// <summary>Rebuilds history from log lines written before the history file existed.</summary>
    private static SleepHistory ImportFromLog()
    {
        var history = new SleepHistory { Imported = true };
        foreach (string path in new[] { AppPaths.LogFile + ".old", AppPaths.LogFile })
        {
            foreach (string line in LogFile.ReadLines(path))
            {
                if (!LogFile.TryParse(line, out DateTime local, out _, out string msg)) continue;
                DateTime utc = local.ToUniversalTime();

                if (msg.StartsWith("Started.", StringComparison.Ordinal))
                {
                    history._pending = null; // a new engine process
                }
                else if (msg.StartsWith("Sleeping: idle for ", StringComparison.Ordinal))
                {
                    long? idle = ParseDuration(msg["Sleeping: idle for ".Length..]);
                    history.Begin(new SleepEvent
                    {
                        SleptUtc = utc,
                        Trigger = SleepTrigger.Idle,
                        IdleSeconds = idle,
                        IdleSinceUtc = idle is { } s ? utc.AddSeconds(-s) : null,
                    });
                }
                else if (msg.StartsWith("Sleeping: ", StringComparison.Ordinal))
                {
                    history.Begin(new SleepEvent { SleptUtc = utc, Trigger = SleepTrigger.Manual });
                }
                else if (msg.StartsWith("SetSuspendState failed (Win32 error ", StringComparison.Ordinal))
                {
                    string rest = msg["SetSuspendState failed (Win32 error ".Length..];
                    history.Refused(int.TryParse(rest.AsSpan(0, Math.Max(0, rest.IndexOf(')'))), out int err) ? err : 0);
                }
                else if (msg == "System suspending")
                {
                    history.Suspending(utc);
                }
                else if (msg is "Resumed" or "Resumed (user)")
                {
                    history.Woke(utc);
                }
            }
        }
        history._pending = null;
        return history;
    }

    /// <summary>Parses <see cref="Format.Duration"/> output ("1h 05m", "10m 00s", "45s") into seconds.</summary>
    private static long? ParseDuration(string text)
    {
        long total = 0;
        foreach (string part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length < 2 || !long.TryParse(part.AsSpan(0, part.Length - 1), out long n)) return null;
            total += part[^1] switch { 'h' => n * 3600, 'm' => n * 60, 's' => n, _ => 0 };
        }
        return total;
    }
}
