using System.Diagnostics;
using Drowsy.Interop;

namespace Drowsy.Activity;

/// <summary>Cheap system-wide checks: full-screen apps, display-on requests, blocking processes.</summary>
internal static unsafe class SystemStateSource
{
    private const int QUNS_BUSY = 2;                    // full-screen app or presentation settings
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3; // exclusive full-screen Direct3D (games)
    private const int QUNS_PRESENTATION_MODE = 4;

    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    public static string? FullscreenState()
    {
        int state;
        if (Native.SHQueryUserNotificationState(&state) != 0) return null;
        return state switch
        {
            QUNS_BUSY => "full-screen window",
            QUNS_RUNNING_D3D_FULL_SCREEN => "full-screen game (Direct3D)",
            QUNS_PRESENTATION_MODE => "presentation mode",
            _ => null,
        };
    }

    /// <summary>
    /// True when some app has asked Windows to keep the display on (typical for video playback and
    /// games). System-required requests are deliberately ignored: those are what audio drivers hold.
    /// </summary>
    public static bool DisplayRequested()
    {
        uint state = 0;
        int status = Native.CallNtPowerInformation(Native.SystemExecutionState, null, 0, &state, sizeof(uint));
        return status == 0 && (state & ES_DISPLAY_REQUIRED) != 0;
    }

    public static string? RunningBlockingProcess(IReadOnlyList<string> names)
    {
        foreach (string name in names)
        {
            string bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            Process[] found = Process.GetProcessesByName(bare);
            bool running = found.Length > 0;
            foreach (Process p in found) p.Dispose();
            if (running) return bare;
        }
        return null;
    }
}
