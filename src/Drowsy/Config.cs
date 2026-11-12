using System.Text.Json;
using System.Text.Json.Serialization;

namespace Drowsy;

/// <summary>User settings, stored as JSON in %APPDATA%\Drowsy\config.json.</summary>
public sealed class Config
{
    /// <summary>Master switch. When false Drowsy keeps watching but never puts the PC to sleep.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minutes with no activity from any enabled source before the PC sleeps.</summary>
    public int IdleMinutes { get; set; } = 20;

    /// <summary>Seconds before sleeping to show a warning notification (0 = no warning).</summary>
    public int WarningSeconds { get; set; } = 60;

    /// <summary>Treat audible output on any playback device as activity.</summary>
    public bool AudioIsActivity { get; set; } = true;

    /// <summary>Peak level (0..1) above which a playback device counts as playing. 0.001 ≈ -60 dBFS.</summary>
    public float AudioPeakThreshold { get; set; } = 0.001f;

    /// <summary>Treat an app with the microphone open (calls, recording, dictation) as activity.</summary>
    public bool MicrophoneIsActivity { get; set; } = true;

    /// <summary>Seconds of audible output (within any 10-second window) before audio counts. Filters notification pings.</summary>
    public int AudioMinSeconds { get; set; } = 3;

    /// <summary>
    /// Apps whose audio and microphone use never count (process name without .exe, or "System sounds").
    /// Audio mixers are ignored by default: their output sessions carry everything already measured
    /// at the source apps (plus mic monitoring / room noise), and they hold the mic open permanently.
    /// </summary>
    public List<string> IgnoredAudioApps { get; set; } = ["Elgato.WaveLink"];

    /// <summary>Treat a full-screen app (game, video, presentation) in the foreground as activity.</summary>
    public bool FullscreenIsActivity { get; set; } = true;

    /// <summary>Treat XInput (Xbox-compatible) controller input as activity.</summary>
    public bool ControllerIsActivity { get; set; } = true;

    /// <summary>Treat apps asking Windows to keep the display on (video players, games) as activity.</summary>
    public bool HonorDisplayRequests { get; set; } = true;

    /// <summary>Process names (without .exe) that keep the PC awake while running.</summary>
    public List<string> BlockingProcesses { get; set; } = [];

    /// <summary>Ignore synthesized input (SendInput / remote-control software) that has no physical device.</summary>
    public bool IgnoreInjectedInput { get; set; }

    /// <summary>Check GitHub for a new version once a day and offer to install it.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Raw-input device paths to ignore (case-insensitive substring, e.g. "VID_1852&amp;PID_50D1").</summary>
    public List<string> IgnoredInputDevices { get; set; } = [];
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Config))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext
{
}

/// <summary>Loads the config file and reloads it when it changes on disk.</summary>
internal sealed class ConfigStore
{
    private DateTime _lastWrite;

    public ConfigStore()
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        if (!File.Exists(AppPaths.ConfigFile))
        {
            Save(new Config());
            Log.Info($"Created default config at {AppPaths.ConfigFile}");
        }
        Current = Load() ?? new Config();
    }

    public Config Current { get; private set; }

    /// <summary>Returns true when the file changed and was reloaded successfully.</summary>
    public bool ReloadIfChanged()
    {
        DateTime write;
        try { write = File.GetLastWriteTimeUtc(AppPaths.ConfigFile); }
        catch { return false; }
        if (write == _lastWrite) return false;

        if (Load() is { } cfg)
        {
            Current = cfg;
            Log.Info("Config reloaded");
            return true;
        }
        return false;
    }

    private Config? Load()
    {
        try
        {
            _lastWrite = File.GetLastWriteTimeUtc(AppPaths.ConfigFile);
            string json = File.ReadAllText(AppPaths.ConfigFile);
            Config cfg = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config) ?? new Config();
            cfg.IdleMinutes = Math.Clamp(cfg.IdleMinutes, 1, 24 * 60);
            cfg.WarningSeconds = Math.Clamp(cfg.WarningSeconds, 0, 600);
            cfg.AudioMinSeconds = Math.Clamp(cfg.AudioMinSeconds, 0, 10);
            return cfg;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {AppPaths.ConfigFile}; keeping previous settings", ex);
            return null;
        }
    }

    public static void Save(Config cfg)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        string tmp = AppPaths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, ConfigJsonContext.Default.Config));
        File.Move(tmp, AppPaths.ConfigFile, overwrite: true); // the engine never reads a half-written file
    }
}

internal static class AppPaths
{
    public static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Drowsy");
    public static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");

    public static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Drowsy");
    public static readonly string LogFile = Path.Combine(LogDir, "drowsy.log");

    /// <summary>The folder Drowsy runs from.</summary>
    public static string AppDir => AppContext.BaseDirectory;

    /// <summary>Drowsy.exe: what Start with Windows, the installer and updates point at.</summary>
    public static string EngineExe => Path.Combine(AppDir, "Drowsy.exe");
}
