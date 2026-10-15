namespace Proxyz.Core.Models;

public enum UnknownNetworkAction { KeepCurrent, Direct }

public sealed record UserPreferences
{
    public bool AutomationEnabled { get; init; }
    public bool Paused { get; init; }
    public bool ObserveWifi { get; init; }
    public bool PauseOnVpn { get; init; } = true;
    public UnknownNetworkAction UnknownNetwork { get; init; } = UnknownNetworkAction.KeepCurrent;
    public double UiScale { get; init; } = 1.0;
}

public sealed record AppConfig
{
    public int SchemaVersion { get; init; } = 1;
    public List<Profile> Profiles { get; init; } = [];
    public List<NetworkRule> Rules { get; init; } = [];
    public UserPreferences Preferences { get; init; } = new();
}
