using System.Runtime.InteropServices;

namespace Drowsy.Interop;

// ---- Structures -------------------------------------------------------------------------

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public char* lpszMenuName;
    public char* lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RAWINPUTDEVICE
{
    public ushort usUsagePage;
    public ushort usUsage;
    public uint dwFlags;
    public nint hwndTarget;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RAWINPUTDEVICELIST
{
    public nint hDevice;
    public uint dwType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RAWINPUTHEADER
{
    public uint dwType;
    public uint dwSize;
    public nint hDevice;
    public nint wParam;
}

// RAWMOUSE has a 4-byte-aligned union after usFlags, hence the explicit layout.
[StructLayout(LayoutKind.Explicit)]
internal struct RAWMOUSE
{
    [FieldOffset(0)] public ushort usFlags;
    [FieldOffset(4)] public ushort usButtonFlags;
    [FieldOffset(6)] public ushort usButtonData;
    [FieldOffset(8)] public uint ulRawButtons;
    [FieldOffset(12)] public int lLastX;
    [FieldOffset(16)] public int lLastY;
    [FieldOffset(20)] public uint ulExtraInformation;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public nint hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public nint hIcon;
    public fixed char szTip[128];
    public uint dwState;
    public uint dwStateMask;
    public fixed char szInfo[256];
    public uint uVersion; // union with uTimeout
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags;
    public Guid guidItem;
    public nint hBalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XINPUT_STATE
{
    public uint dwPacketNumber;
    public ushort wButtons;
    public byte bLeftTrigger;
    public byte bRightTrigger;
    public short sThumbLX;
    public short sThumbLY;
    public short sThumbRX;
    public short sThumbRY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TOKEN_PRIVILEGES
{
    public uint PrivilegeCount;
    public uint LuidLowPart;
    public int LuidHighPart;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT
{
    public ushort vt;
    public ushort wReserved1, wReserved2, wReserved3;
    public nint p;
    public nint p2;
}

// ---- Functions --------------------------------------------------------------------------

internal static unsafe partial class Native
{
    // Window messages
    public const uint WM_NULL = 0x0000;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_ENDSESSION = 0x0016;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_INPUT = 0x00FF;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_POWERBROADCAST = 0x0218;
    public const uint WM_USER = 0x0400;
    public const uint WM_APP = 0x8000;

    // Power broadcast
    public const nint PBT_APMSUSPEND = 0x0004;
    public const nint PBT_APMRESUMESUSPEND = 0x0007;
    public const nint PBT_APMRESUMEAUTOMATIC = 0x0012;

    // Raw input
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIDI_DEVICENAME = 0x20000007;
    public const uint RIM_TYPEMOUSE = 0;
    public const uint RIM_TYPEKEYBOARD = 1;
    public const ushort MOUSE_MOVE_ABSOLUTE = 0x01;

    // Menus
    public const uint MF_STRING = 0x0000;
    public const uint MF_GRAYED = 0x0001;
    public const uint MF_CHECKED = 0x0008;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_RETURNCMD = 0x0100;

    // Shell_NotifyIcon
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    public const uint NIIF_INFO = 0x01, NIIF_NOSOUND = 0x10;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint NIN_SELECT = WM_USER + 0;
    public const uint NIN_KEYSELECT = WM_USER + 1;
    public const uint NIN_BALLOONUSERCLICK = WM_USER + 5;

    public const uint MB_OK = 0x0, MB_YESNO = 0x4, MB_ICONINFORMATION = 0x40, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20;
    public const int IDYES = 6;

    // user32 ------------------------------------------------------------------------------
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowW(string className, string? windowName);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial ushort RegisterClassExW(WNDCLASSEXW* wc);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    public static partial int DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    public static partial int TranslateMessage(MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    public static partial int PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    public static partial nuint SetTimer(nint hwnd, nuint id, uint elapseMs, nint timerProc);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int RegisterRawInputDevices(RAWINPUTDEVICE* devices, uint count, uint size);

    [LibraryImport("user32.dll")]
    public static partial uint GetRawInputData(nint hRawInput, uint command, void* data, uint* size, uint headerSize);

    [LibraryImport("user32.dll")]
    public static partial uint GetRawInputDeviceInfoW(nint hDevice, uint command, void* data, uint* size);

    [LibraryImport("user32.dll")]
    public static partial uint GetRawInputDeviceList(RAWINPUTDEVICELIST* list, uint* count, uint size);

    // hid.dll / CreateFile: read a HID device's product string ("Razer Viper V3 Pro").
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("hid.dll")]
    public static partial byte HidD_GetProductString(nint device, void* buffer, uint bufferBytes);

    [LibraryImport("user32.dll")]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);

    [LibraryImport("user32.dll")]
    public static partial int DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    public static partial int SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial int GetCursorPos(POINT* pt);

    [LibraryImport("user32.dll")]
    public static partial int DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBoxW(nint hwnd, string text, string caption, uint type);

    // shell32 -----------------------------------------------------------------------------
    [LibraryImport("shell32.dll")]
    public static partial int Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);

    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(int* state);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ExtractIconExW(string file, int index, nint* large, nint* small, uint count);

    // kernel32 ----------------------------------------------------------------------------
    [LibraryImport("kernel32.dll")]
    public static partial nint GetModuleHandleW(nint name);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    public static partial int CloseHandle(nint handle);

    // advapi32 ----------------------------------------------------------------------------
    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int OpenProcessToken(nint process, uint access, nint* token);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int LookupPrivilegeValueW(string? system, string name, void* luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int AdjustTokenPrivileges(nint token, int disableAll, TOKEN_PRIVILEGES* state, uint len, nint prev, nint retLen);

    // powrprof ----------------------------------------------------------------------------
    [LibraryImport("powrprof.dll", SetLastError = true)]
    public static partial byte SetSuspendState(byte hibernate, byte force, byte wakeupEventsDisabled);

    public const int SystemExecutionState = 16;

    [LibraryImport("powrprof.dll")]
    public static partial int CallNtPowerInformation(int level, void* input, uint inputLen, void* output, uint outputLen);

    // xinput ------------------------------------------------------------------------------
    [LibraryImport("xinput1_4.dll")]
    public static partial uint XInputGetState(uint userIndex, XINPUT_STATE* state);

    // ole32 -------------------------------------------------------------------------------
    public const uint COINIT_APARTMENTTHREADED = 0x2;
    public const uint CLSCTX_ALL = 0x17;

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(Guid* clsid, nint outer, uint clsContext, Guid* iid, nint* ppv);

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(PROPVARIANT* pv);
}
