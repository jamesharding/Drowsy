using System.Runtime.InteropServices;
using Drowsy.Interop;

namespace Drowsy.Update;

/// <summary>
/// First-run install: when Drowsy is started from a temp or Downloads folder (straight out of the
/// zip), offer to copy it to %LOCALAPPDATA%\Programs\Drowsy, add a Start menu shortcut and turn on
/// Start with Windows. Per-user, no admin rights. Running it from anywhere else stays portable.
/// </summary>
internal static class Installer
{
    public static readonly string InstallDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Drowsy");

    private static readonly string ShortcutPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Drowsy.lnk");

    /// <summary>
    /// Asks, and installs if the user agrees. Returns true when the installed copy has been started
    /// and this process should exit.
    /// </summary>
    public static bool OfferIfNeeded()
    {
        if (!Startup.RunningFromTransientFolder) return false;

        int answer = Native.MessageBoxW(0,
            $"Install Drowsy?\n\nIt will be copied to {InstallDir}, added to the Start menu and set to start with Windows. " +
            "No admin rights needed.\n\nChoose No to just run it from here this time.",
            "Drowsy", Native.MB_YESNO | Native.MB_ICONQUESTION);
        if (answer != Native.IDYES) return false;

        try
        {
            Updater.ReplaceFiles(Updater.AppDir, InstallDir);
            string exe = Path.Combine(InstallDir, "Drowsy.exe");
            CreateShortcut(exe);
            Log.Info($"Installed to {InstallDir}");

            // The installed copy waits for this process to release the single-instance lock, then
            // starts the engine with --installed (which turns on Start with Windows from the new location).
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--restart --installed") { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Install failed", ex);
            Native.MessageBoxW(0, $"Couldn't install Drowsy:\n\n{ex.Message}\n\nIt will run from here instead.",
                "Drowsy", Native.MB_OK | Native.MB_ICONERROR);
            return false;
        }
    }

    // IShellLinkW / IPersistFile through their vtables: no COM interop generation needed (NativeAOT-safe).
    private static unsafe void CreateShortcut(string exe)
    {
        Guid clsid = new("00021401-0000-0000-C000-000000000046"); // CLSID_ShellLink
        Guid iidLink = new("000214F9-0000-0000-C000-000000000046"); // IID_IShellLinkW
        Guid iidPersist = new("0000010b-0000-0000-C000-000000000046"); // IID_IPersistFile
        const uint CLSCTX_INPROC_SERVER = 1;

        Native.CoInitializeEx(0, Native.COINIT_APARTMENTTHREADED);
        nint link;
        Marshal.ThrowExceptionForHR(Native.CoCreateInstance(&clsid, 0, CLSCTX_INPROC_SERVER, &iidLink, &link));
        try
        {
            void** vt = *(void***)link;
            fixed (char* path = exe)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int>)vt[20])(link, path)); // SetPath
            fixed (char* dir = Path.GetDirectoryName(exe))
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int>)vt[9])(link, dir)); // SetWorkingDirectory
            fixed (char* desc = "Puts Windows to sleep when you're genuinely idle")
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int>)vt[7])(link, desc)); // SetDescription

            nint persist;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vt[0])(link, &iidPersist, &persist)); // QueryInterface
            try
            {
                void** pvt = *(void***)persist;
                Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
                fixed (char* lnk = ShortcutPath)
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)pvt[6])(persist, lnk, 1)); // Save
            }
            finally
            {
                Marshal.Release(persist);
            }
        }
        finally
        {
            Marshal.Release(link);
        }
    }
}
