using Proxyz.Core.Models;

namespace Proxyz.Core;

public interface IProxyBackend
{
    ProxySettings Read();
    void Write(ProxySettings settings);
    PolicyStatus ReadPolicy();
}

public interface IStateStore
{
    AppConfig LoadConfig();
    void SaveConfig(AppConfig config);
    ChangeJournal LoadJournal();
    void SaveJournal(ChangeJournal journal);
}

public interface IAuditLog
{
    // Do not log PAC URLs, proxy addresses, SSIDs, credentials, or full settings.
    void Record(string action, string outcome, Guid? profileId = null);
}

public sealed class NullAuditLog : IAuditLog
{
    public void Record(string action, string outcome, Guid? profileId = null) { }
}
