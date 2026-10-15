using Proxyz.Core.Models;

namespace Proxyz.Cli;

public interface ICommandPlatform
{
    bool Demo { get; }
    bool Resident { get; }
    bool AutomationSuspended => false;
    NetworkSnapshot ReadNetwork();
    bool StartupEnabled { get; }
    string? StartupCommand => StartupEnabled ? "registered" : null;
    string StartupTarget => "Proxyz.exe tray --host";
    void SetStartup(bool enabled);
    CommandResult Check(Uri? target);
    void Quit();
}

public sealed record CommandResult(int ExitCode, string Text, object? Data = null);
public sealed record StatusInfo(string State, bool AutomationEnabled, string Reason, ProxySettings Current,
    string? Profile, NetworkSnapshot Network, bool RecoveryRequired, bool ExternalChange, string PolicyNotice, IReadOnlyList<RuleMatch> Matches);
public sealed record RuleMatch(Guid NetworkId, string Network, string Rule, string Profile);

public sealed class CommandPlan(string preview, bool requiresConfirmation, Func<CommandResult> execute) : IDisposable
{
    private bool _used;
    public string Preview { get; } = preview;
    public bool RequiresConfirmation { get; } = requiresConfirmation;
    public CommandResult Execute()
    {
        if (_used) throw new InvalidOperationException("This preview has already been used.");
        _used = true;
        return execute();
    }
    public void Dispose() => _used = true;
}

public static class ProxyDescription
{
    // Include inactive strings: recovery must preview every value being written.
    public static string Format(ProxySettings settings) =>
        $"{settings.Summary}\nFlags: {settings.Flags}\nServer: {settings.ProxyServer}\nBypass: {settings.ProxyBypass}\nPAC: {settings.AutoConfigUrl}";
}
