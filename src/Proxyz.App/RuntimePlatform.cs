using Proxyz.App.Infrastructure;
using Proxyz.Cli;
using Proxyz.Core.Models;
using Proxyz.Windows;

namespace Proxyz.App;

internal sealed class RuntimePlatform(AppRuntime runtime, bool resident, Action quit, Func<bool>? suspended = null) : ICommandPlatform
{
    public bool Demo => runtime.Demo;
    public bool Resident => resident;
    public bool AutomationSuspended => suspended?.Invoke() ?? false;
    private bool _demoStartup;
    public NetworkSnapshot ReadNetwork() => runtime.ReadNetwork();
    public bool StartupEnabled => Demo ? _demoStartup : StartupRegistration.IsRegistered();
    public string? StartupCommand => Demo ? (_demoStartup ? "DEMO tray" : null) : StartupRegistration.ReadCommand();
    public string StartupTarget => Demo ? "DEMO tray" : $"\"{Environment.ProcessPath}\" tray --host";
    public void SetStartup(bool enabled) { if (Demo) _demoStartup = enabled; else StartupRegistration.Set(enabled); }
    public CommandResult Check(Uri? target)
    {
        if (Demo) return new(0, "DEMO: WinINet / WinHTTP 診断は架空です。通信・設定変更なし。");
        var result = ConnectionDiagnostics.Check(target);
        return new(result.Success ? 0 : 4, result.Text);
    }
    public void Quit() => quit();
}
