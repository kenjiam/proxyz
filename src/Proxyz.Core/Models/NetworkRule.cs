using System.Text.Json.Serialization;

namespace Proxyz.Core.Models;

public sealed record NetworkRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public Guid ProfileId { get; init; }
    public bool Enabled { get; init; } = true;
    public Guid? NetworkId { get; init; }
    public string? SsidHex { get; init; }
    public NetworkKind? Kind { get; init; }
    [JsonIgnore] public string ConditionSummary =>
        string.Join(" ＋ ", new[]
        {
            NetworkId.HasValue ? "登録済みの Windows ネットワーク" : null,
            SsidHex is not null ? "SSID の完全一致" : null,
            Kind?.ToString()
        }.Where(s => s is not null));

    public bool Matches(NetworkIdentity network) => Enabled &&
        (NetworkId.HasValue || !string.IsNullOrEmpty(SsidHex)) &&
        (!NetworkId.HasValue || NetworkId.Value == network.NetworkId) &&
        (Kind is null || Kind == network.Kind) &&
        (SsidHex is null || string.Equals(SsidHex, network.SsidHex, StringComparison.OrdinalIgnoreCase));
}
