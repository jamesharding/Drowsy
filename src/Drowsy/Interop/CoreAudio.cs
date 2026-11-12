using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Drowsy.Interop;

// Minimal Core Audio (MMDevice API) surface, using source-generated COM interop.
// Only the leading vtable slots we call are declared; order must match the SDK headers.

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    IMMDeviceCollection EnumAudioEndpoints(int dataFlow, uint stateMask);
}

[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    uint GetCount();
    IMMDevice Item(uint index);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    nint Activate(in Guid iid, uint clsCtx, nint activationParams);
    IPropertyStore OpenPropertyStore(uint access);
}

[GeneratedComInterface]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
internal partial interface IPropertyStore
{
    uint GetCount();
    PROPERTYKEY GetAt(uint index);
    void GetValue(in PROPERTYKEY key, out PROPVARIANT value);
}

[GeneratedComInterface]
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
internal partial interface IAudioMeterInformation
{
    float GetPeakValue();
}

[GeneratedComInterface]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
internal partial interface IAudioSessionManager2
{
    // IAudioSessionManager slots (unused; declared to keep the vtable order)
    [PreserveSig] int GetAudioSessionControl(nint sessionGuid, uint flags, out nint control);
    [PreserveSig] int GetSimpleAudioVolume(nint sessionGuid, uint flags, out nint volume);

    IAudioSessionEnumerator GetSessionEnumerator();
}

[GeneratedComInterface]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
internal partial interface IAudioSessionEnumerator
{
    int GetCount();
    nint GetSession(int index); // raw IAudioSessionControl*, caller releases
}

[GeneratedComInterface]
[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
internal partial interface IAudioSessionControl2
{
    // IAudioSessionControl slots
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out nint name);
    [PreserveSig] int SetDisplayName(nint value, in Guid eventContext);
    [PreserveSig] int GetIconPath(out nint path);
    [PreserveSig] int SetIconPath(nint value, in Guid eventContext);
    [PreserveSig] int GetGroupingParam(out Guid grouping);
    [PreserveSig] int SetGroupingParam(in Guid grouping, in Guid eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(nint notifications);
    [PreserveSig] int UnregisterAudioSessionNotification(nint notifications);

    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier(out nint id);
    [PreserveSig] int GetSessionInstanceIdentifier(out nint id);
    [PreserveSig] int GetProcessId(out uint processId);
    [PreserveSig] int IsSystemSoundsSession(); // S_OK (0) = system sounds, S_FALSE (1) = not
}

internal static class CoreAudio
{
    public const int AudioSessionStateExpired = 2;
    public static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    public const int eRender = 0;
    public const int eCapture = 1;
    public const int AudioSessionStateActive = 1;
    public const uint DEVICE_STATE_ACTIVE = 0x1;
    public const ushort VT_LPWSTR = 31;

    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    public static readonly PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14,
    };

    public static readonly StrategyBasedComWrappers Wrappers = new();

    public static unsafe IMMDeviceEnumerator CreateEnumerator()
    {
        Guid clsid = CLSID_MMDeviceEnumerator, iid = IID_IMMDeviceEnumerator;
        nint p;
        Marshal.ThrowExceptionForHR(Native.CoCreateInstance(&clsid, 0, Native.CLSCTX_ALL, &iid, &p));
        try
        {
            return (IMMDeviceEnumerator)Wrappers.GetOrCreateObjectForComInstance(p, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(p);
        }
    }

    public static IAudioMeterInformation ActivateMeter(IMMDevice device)
    {
        nint p = device.Activate(IID_IAudioMeterInformation, Native.CLSCTX_ALL, 0);
        try
        {
            return (IAudioMeterInformation)Wrappers.GetOrCreateObjectForComInstance(p, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(p);
        }
    }

    public static IAudioSessionManager2 ActivateSessionManager(IMMDevice device)
    {
        nint p = device.Activate(IID_IAudioSessionManager2, Native.CLSCTX_ALL, 0);
        try
        {
            return (IAudioSessionManager2)Wrappers.GetOrCreateObjectForComInstance(p, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(p);
        }
    }

    /// <summary>Wraps a raw COM pointer (taking over the caller's reference).</summary>
    public static object WrapAndRelease(nint p)
    {
        try
        {
            return Wrappers.GetOrCreateObjectForComInstance(p, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(p);
        }
    }

    public static unsafe string GetFriendlyName(IMMDevice device)
    {
        IPropertyStore store = device.OpenPropertyStore(0 /* STGM_READ */);
        store.GetValue(PKEY_Device_FriendlyName, out PROPVARIANT pv);
        try
        {
            return pv.vt == VT_LPWSTR ? Marshal.PtrToStringUni(pv.p) ?? "(unnamed)" : "(unnamed)";
        }
        finally
        {
            Native.PropVariantClear(&pv);
        }
    }

    public static void Release(object? comObject)
    {
        if (comObject is ComObject c)
        {
            c.FinalRelease();
        }
    }
}
