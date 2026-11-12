using System.Text.Json;
using System.Text.Json.Serialization;
using Drowsy.Activity;

namespace Drowsy;

/// <summary>
/// Live status the engine publishes to %LOCALAPPDATA%\Drowsy\status.json while the settings
/// window is open, so the window can show what is keeping the PC awake.
/// </summary>
public sealed class EngineStatus
{
    public DateTime UpdatedUtc { get; set; }

    /// <summary>"Awake", "Idle", "SleepingSoon", "Paused" or "Grace".</summary>
    public string State { get; set; } = "";

    /// <summary>Short title: "Staying awake", "Sleeping in 17 min", "Paused".</summary>
    public string Title { get; set; } = "";

    /// <summary>One-line explanation under the title.</summary>
    public string Headline { get; set; } = "";

    /// <summary>Plain-English reasons the PC is being kept awake right now (empty when idle).</summary>
    public List<string> Reasons { get; set; } = [];

    /// <summary>Most recent activity, for the idle view ("Keyboard or mouse in use, 3m ago").</summary>
    public string? LastActivity { get; set; }

    public long IdleSeconds { get; set; }
    public long LimitSeconds { get; set; }

    /// <summary>Seconds until sleep, or null when paused / something is keeping it awake.</summary>
    public long? SleepInSeconds { get; set; }

    public List<SignalStatus> Signals { get; set; } = [];
}

public sealed class SignalStatus
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Active { get; set; }
    public long? LastSeenSecondsAgo { get; set; }
    public string? Detail { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EngineStatus))]
internal sealed partial class StatusJsonContext : JsonSerializerContext
{
}

internal static class StatusFile
{
    public static readonly string Path = System.IO.Path.Combine(AppPaths.LogDir, "status.json");

    public static void Write(EngineStatus status)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(status, StatusJsonContext.Default.EngineStatus));
            File.Move(tmp, Path, overwrite: true); // atomic replace: readers never see a partial file
        }
        catch
        {
            // Best effort: the settings window just shows stale/no status.
        }
    }

    public static EngineStatus? Read()
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(Path), StatusJsonContext.Default.EngineStatus);
        }
        catch
        {
            return null;
        }
    }

    public static SignalStatus ToStatus(SignalState s) => new()
    {
        Name = Format.Name(s.Signal),
        Enabled = s.Enabled,
        Active = s.Active,
        LastSeenSecondsAgo = s.AgoMs is { } ms ? ms / 1000 : null,
        Detail = Format.FriendlyDetail(s.Signal, s.Detail),
    };
}
