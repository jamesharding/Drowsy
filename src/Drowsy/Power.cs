using System.Runtime.InteropServices;
using Drowsy.Interop;

namespace Drowsy;

internal static unsafe class Power
{
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002;

    /// <summary>
    /// Requests normal sleep (S3 / Modern Standby), exactly like Start → Power → Sleep.
    /// This bypasses Windows' idle-sleep logic, which is what gets blocked by power requests.
    /// </summary>
    public static bool TrySleep(out int win32Error)
    {
        EnableShutdownPrivilege();
        bool ok = Native.SetSuspendState(hibernate: 0, force: 0, wakeupEventsDisabled: 0) != 0;
        win32Error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    // SetSuspendState needs SeShutdownPrivilege enabled in our token (users hold it, disabled).
    private static void EnableShutdownPrivilege()
    {
        nint token;
        if (Native.OpenProcessToken(Native.GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token) == 0) return;
        try
        {
            TOKEN_PRIVILEGES tp = default;
            if (Native.LookupPrivilegeValueW(null, "SeShutdownPrivilege", &tp.LuidLowPart) == 0) return;
            tp.PrivilegeCount = 1;
            tp.Attributes = SE_PRIVILEGE_ENABLED;
            Native.AdjustTokenPrivileges(token, 0, &tp, 0, 0, 0);
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }
}
