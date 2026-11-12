using System.Diagnostics;
using Drowsy.Interop;

namespace Drowsy.Activity;

/// <summary>Cheap system-wide checks: display-on requests and blocking processes.</summary>
internal static unsafe class SystemStateSource
{
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

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
