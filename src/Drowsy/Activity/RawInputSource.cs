using System.ComponentModel;
using System.Runtime.InteropServices;
using Drowsy.Interop;

namespace Drowsy.Activity;

/// <summary>
/// Keyboard and mouse activity via the Raw Input API. Unlike GetLastInputInfo, raw input tells us
/// which physical device produced each event, so phantom input can be identified and ignored, and
/// non-keyboard/mouse HID devices (media knobs, consumer controls) are never counted.
/// </summary>
internal sealed unsafe class RawInputSource
{
    private readonly Dictionary<nint, string> _names = [];
    private int _lastAbsX = int.MinValue, _lastAbsY = int.MinValue;

    public static void Register(nint hwnd)
    {
        RAWINPUTDEVICE* devices = stackalloc RAWINPUTDEVICE[2];
        devices[0] = new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = hwnd }; // mouse
        devices[1] = new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = hwnd }; // keyboard
        if (Native.RegisterRawInputDevices(devices, 2, (uint)sizeof(RAWINPUTDEVICE)) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterRawInputDevices failed");
        }
    }

    /// <summary>
    /// Decodes a WM_INPUT message. Returns true when it represents real user activity
    /// (movement, a click, a key) rather than a no-op report.
    /// </summary>
    public bool TryRead(nint hRawInput, out nint device, out string kind)
    {
        device = 0;
        kind = "unknown";

        uint headerSize = (uint)sizeof(RAWINPUTHEADER);
        uint size = 0;
        Native.GetRawInputData(hRawInput, Native.RID_INPUT, null, &size, headerSize);
        if (size == 0 || size > 1024) return false;

        byte* buffer = stackalloc byte[(int)size];
        if (Native.GetRawInputData(hRawInput, Native.RID_INPUT, buffer, &size, headerSize) == uint.MaxValue) return false;

        var header = (RAWINPUTHEADER*)buffer;
        device = header->hDevice;

        switch (header->dwType)
        {
            case Native.RIM_TYPEKEYBOARD:
                kind = "keyboard";
                return true;

            case Native.RIM_TYPEMOUSE:
                kind = "mouse";
                var mouse = (RAWMOUSE*)(buffer + headerSize);
                if (mouse->usButtonFlags != 0) return true;
                if ((mouse->usFlags & Native.MOUSE_MOVE_ABSOLUTE) != 0)
                {
                    // Absolute devices (tablets, remote desktop, KVM software) report position, not deltas.
                    bool moved = mouse->lLastX != _lastAbsX || mouse->lLastY != _lastAbsY;
                    _lastAbsX = mouse->lLastX;
                    _lastAbsY = mouse->lLastY;
                    return moved;
                }
                return mouse->lLastX != 0 || mouse->lLastY != 0;

            default:
                return false;
        }
    }

    /// <summary>A connected keyboard or mouse, for the settings window's device picker.</summary>
    public readonly record struct InputDevice(string Key, string Name, string Kind);

    /// <summary>
    /// Connected keyboards and mice. <see cref="InputDevice.Key"/> is "VID_xxxx&amp;PID_xxxx" when
    /// available (stable across reconnects/ports), otherwise the device path.
    /// </summary>
    public static List<InputDevice> ListDevices()
    {
        var result = new Dictionary<string, InputDevice>(StringComparer.OrdinalIgnoreCase);
        uint count = 0, itemSize = (uint)sizeof(RAWINPUTDEVICELIST);
        Native.GetRawInputDeviceList(null, &count, itemSize);
        if (count == 0 || count > 512) return [];

        RAWINPUTDEVICELIST* list = stackalloc RAWINPUTDEVICELIST[(int)count];
        if (Native.GetRawInputDeviceList(list, &count, itemSize) == uint.MaxValue) return [];

        for (int i = 0; i < count; i++)
        {
            if (list[i].dwType is not (Native.RIM_TYPEMOUSE or Native.RIM_TYPEKEYBOARD)) continue;
            string path = PathOf(list[i].hDevice);
            if (path.Length == 0) continue;

            string key = VidPid(path) ?? path;
            string kind = list[i].dwType == Native.RIM_TYPEMOUSE ? "Mouse" : "Keyboard";
            string name = ProductName(path) ?? (key == path ? $"{kind} (no ID)" : $"{kind} {key.Replace('&', ' ')}");

            // A single physical device often exposes several collections (keyboards commonly add a
            // mouse interface for knobs/macros); keep one entry, preferring a real product name.
            if (result.TryGetValue(key, out InputDevice existing))
            {
                string mergedKind = existing.Kind == kind || existing.Kind == "Keyboard + mouse" ? existing.Kind : "Keyboard + mouse";
                bool existingIsGeneric = existing.Name.StartsWith(existing.Kind, StringComparison.Ordinal);
                result[key] = new InputDevice(key, existingIsGeneric ? name : existing.Name, mergedKind);
            }
            else
            {
                result[key] = new InputDevice(key, name, kind);
            }
        }
        return result.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"VID_1532&amp;PID_00C1" from a device path, or null.</summary>
    public static string? VidPid(string path)
    {
        int vid = path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        int pid = path.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (vid < 0 || pid < 0 || vid + 8 > path.Length || pid + 8 > path.Length) return null;
        return $"{path.Substring(vid, 8)}&{path.Substring(pid, 8)}".ToUpperInvariant();
    }

    private static string PathOf(nint device)
    {
        uint chars = 0;
        Native.GetRawInputDeviceInfoW(device, Native.RIDI_DEVICENAME, null, &chars);
        if (chars is 0 or > 1024) return "";
        char* text = stackalloc char[(int)chars + 1];
        return Native.GetRawInputDeviceInfoW(device, Native.RIDI_DEVICENAME, text, &chars) == uint.MaxValue ? "" : new string(text);
    }

    private static string? ProductName(string path)
    {
        // Zero access rights are enough to query HID strings, and work for keyboards/mice
        // that Windows otherwise keeps open exclusively.
        nint handle = Native.CreateFileW(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, 0, Native.OPEN_EXISTING, 0, 0);
        if (handle == -1 || handle == 0) return null;
        try
        {
            char* buffer = stackalloc char[128];
            if (Native.HidD_GetProductString(handle, buffer, 128 * sizeof(char)) == 0) return null;
            string name = new string(buffer).Trim();
            return name.Length > 0 ? name : null;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>Device interface path (contains VID/PID), or a marker for injected input.</summary>
    public string DeviceName(nint device)
    {
        if (device == 0) return "injected (no physical device)";
        if (_names.TryGetValue(device, out string? cached)) return cached;

        string name = "(unknown device)";
        uint chars = 0;
        Native.GetRawInputDeviceInfoW(device, Native.RIDI_DEVICENAME, null, &chars);
        if (chars is > 0 and < 1024)
        {
            char* text = stackalloc char[(int)chars + 1];
            if (Native.GetRawInputDeviceInfoW(device, Native.RIDI_DEVICENAME, text, &chars) != uint.MaxValue)
            {
                name = new string(text);
            }
        }
        _names[device] = name;
        return name;
    }
}
