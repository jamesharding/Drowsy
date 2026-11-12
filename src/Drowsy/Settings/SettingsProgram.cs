using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace Drowsy.Settings;

/// <summary>Entry point for <c>Drowsy.exe --settings</c>: a code-only WinUI 3 app (no XAML files).</summary>
internal static class SettingsProgram
{
    public static int Run()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            // (the lambda parameter is named "_", so this assignment needs a real local)
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new SettingsApp();
            GC.KeepAlive(app);
        });
        return 0;
    }
}

/// <summary>
/// Code-only WinUI application. Implementing IXamlMetadataProvider (delegating to the WinUI
/// controls provider) is what lets XamlControlsResources load without a compiled App.xaml.
/// </summary>
internal sealed partial class SettingsApp : Application, IXamlMetadataProvider
{
    private readonly XamlControlsXamlMetaDataProvider _provider = new();
    private SettingsWindow? _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        _window = new SettingsWindow();
        _window.Activate();
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--audio", StringComparison.OrdinalIgnoreCase)))
        {
            _window.OpenAudioMonitor(); // Drowsy.exe --settings --audio
        }
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--updates", StringComparison.OrdinalIgnoreCase)))
        {
            _window.ShowUpdates(); // Drowsy.exe --settings --updates (from the tray)
        }
    }

    public IXamlType GetXamlType(Type type) => _provider.GetXamlType(type);

    public IXamlType GetXamlType(string fullName) => _provider.GetXamlType(fullName);

    public XmlnsDefinition[] GetXmlnsDefinitions() => _provider.GetXmlnsDefinitions();
}
