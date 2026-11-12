using Drowsy.Interop;

namespace Drowsy;

internal static class Program
{
    private const string EngineMutex = @"Local\Drowsy-6F1C2B4E-3A0D-4C8B-9E57-2D41B7A3C9F0";
    private const string SettingsMutex = @"Local\Drowsy-Settings-6F1C2B4E-3A0D-4C8B-9E57-2D41B7A3C9F0";

    /// <summary>
    /// One exe, two modes:
    ///   Drowsy.exe             tray engine (pure Win32, never loads WinUI)
    ///   Drowsy.exe --settings  WinUI 3 settings window (separate process; the engine
    ///                              picks up saved changes by watching config.json)
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Fatal error", e.ExceptionObject as Exception);

        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        if (Has("--diagnose"))
        {
            Diagnose();
            return 0;
        }

        if (Has("--restart"))
        {
            // After an install or update: wait for the old processes, then start the new engine.
            Update.Updater.RunRestartHelper(openSettings: Has("--open-settings"), installed: Has("--installed"));
            return 0;
        }

        bool settings = Has("--settings");

        using var mutex = new Mutex(initiallyOwned: true, settings ? SettingsMutex : EngineMutex, out bool createdNew);
        if (!createdNew)
        {
            if (!settings)
            {
                Native.MessageBoxW(0, "Drowsy is already running — look for the moon icon in the notification area.",
                    "Drowsy", Native.MB_OK | Native.MB_ICONINFORMATION);
            }
            return 0;
        }

        try
        {
            if (settings)
            {
                return ConnectToWindowsAppRuntime() ? Settings.SettingsProgram.Run() : 1;
            }

            Update.Updater.CleanUp(); // files moved aside by the last update
            if (Update.Installer.OfferIfNeeded()) return 0;
            return RunEngine(installed: Has("--installed"));
        }
        catch (Exception ex)
        {
            Log.Error(settings ? "Settings window failed" : "Drowsy failed to start", ex);
            Native.MessageBoxW(0, $"Drowsy failed to start:\n\n{ex.Message}\n\nSee {AppPaths.LogFile}",
                "Drowsy", Native.MB_OK | Native.MB_ICONERROR);
            return 1;
        }
    }

    /// <summary>
    /// Adds the shared Windows App Runtime (which provides WinUI) to this process, as the Windows App
    /// SDK's own start-up code would; it's switched off in Drowsy.csproj so the tray engine never loads
    /// it. If the runtime isn't installed, Windows explains and offers to download it.
    /// </summary>
    private static bool ConnectToWindowsAppRuntime()
    {
        if (Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.TryInitialize(
                Microsoft.WindowsAppSDK.Release.MajorMinor,
                Microsoft.WindowsAppSDK.Release.VersionTag,
                new Microsoft.Windows.ApplicationModel.DynamicDependency.PackageVersion(Microsoft.WindowsAppSDK.Runtime.Version.UInt64),
                Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.InitializeOptions.OnNoMatch_ShowUI,
                out int hr))
        {
            return true;
        }
        Log.Error($"Windows App Runtime {Microsoft.WindowsAppSDK.Release.MajorMinor >> 16}.{Microsoft.WindowsAppSDK.Release.MajorMinor & 0xFFFF} " +
                  $"is not available (0x{hr:X8}); the settings window can't open");
        return false;
    }

    /// <summary><c>Drowsy.exe --diagnose</c>: writes what Drowsy can see to the log.</summary>
    private static void Diagnose()
    {
        Native.CoInitializeEx(0, Native.COINIT_APARTMENTTHREADED);
        Log.Info("--- Diagnose ---");
        foreach (var d in Activity.RawInputSource.ListDevices())
            Log.Info($"Input device: {d.Kind}: {d.Name} [{d.Key}]");
        foreach (string app in Activity.AudioSource.ListSessionApps())
            Log.Info($"Audio session app: {app}");
        Log.Info($"Full-screen state: {Activity.SystemStateSource.FullscreenState() ?? "none"}; display requested: {Activity.SystemStateSource.DisplayRequested()}");
        Log.Info("--- End diagnose ---");
    }

    private static int RunEngine(bool installed)
    {
        // Core Audio COM objects are created and used on this (UI) thread.
        Native.CoInitializeEx(0, Native.COINIT_APARTMENTTHREADED);
        string? welcome = null;
        if (installed)
        {
            try
            {
                Startup.Set(true);
                welcome = "Drowsy is installed and will start with Windows. Click the moon icon for status and settings.";
            }
            catch (InvalidOperationException ex)
            {
                welcome = ex.Message;
            }
        }
        using var app = new TrayApp(new ConfigStore(), welcome);
        return app.Run();
    }

    /// <summary>Asks the running engine (if any) to exit cleanly, removing its tray icon.</summary>
    public static void RequestEngineExit()
    {
        nint hwnd = Native.FindWindowW(TrayApp.WindowClass, null);
        if (hwnd != 0) Native.PostMessageW(hwnd, Native.WM_CLOSE, 0, 0);
    }

    /// <summary>True while a settings window process is running (it holds the settings mutex).</summary>
    public static bool SettingsWindowIsOpen()
    {
        if (!Mutex.TryOpenExisting(SettingsMutex, out Mutex? m)) return false;
        m.Dispose();
        return true;
    }

    /// <summary>True while the tray engine is running.</summary>
    public static bool EngineIsRunning()
    {
        if (!Mutex.TryOpenExisting(EngineMutex, out Mutex? m)) return false;
        m.Dispose();
        return true;
    }

    /// <summary>Starts the tray engine (used by the settings window when it isn't running).</summary>
    public static void LaunchEngine(string? argument = null)
    {
        try
        {
            Start(AppPaths.EngineExe, argument is null ? [] : [argument]);
        }
        catch (Exception ex)
        {
            Log.Error("Could not start Drowsy", ex);
        }
    }

    /// <summary>Opens the settings window in its own process.</summary>
    public static void LaunchSettings(string? extra = null)
    {
        try
        {
            Start(AppPaths.EngineExe, extra is null ? ["--settings"] : ["--settings", extra]);
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the settings window", ex);
        }
    }

    private static void Start(string exe, string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string a in args) info.ArgumentList.Add(a);
        System.Diagnostics.Process.Start(info)?.Dispose();
    }
}
