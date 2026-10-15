using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Proxyz.Core.Models;

public enum NetworkKind { WiFi, Ethernet, Other }

public sealed record NetworkIdentity
{
    public Guid NetworkId { get; init; }
    public Guid AdapterId { get; init; }
    public string Name { get; init; } = "";
    public NetworkKind Kind { get; init; }
    // SSIDs are bytes, not Unicode strings. Match the exact bytes, never a lossy label.
    public string? SsidHex { get; init; }
    public string? SsidLabel { get; init; }
    [JsonIgnore] public string Display => $"{Name} / {KindLabel}" +
        (SsidLabel is null ? "" : $" / Wi-Fi: {SsidLabel}");
    [JsonIgnore] public string KindLabel => Kind switch
    {
        NetworkKind.WiFi => "Wi-Fi", NetworkKind.Ethernet => "有線LAN", _ => "その他の接続"
    };
}

public sealed record NetworkSnapshot
{
    public List<NetworkIdentity> Networks { get; init; } = [];
    public bool Reliable { get; init; } = true;
    public bool VpnDetected { get; init; }
    public string Notice { get; init; } = "";
    // Fingerprint is local and is not a security identity or sent to a server.
    [JsonIgnore] public string Fingerprint => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("|", Networks
            .Select(n => $"{n.NetworkId:N}:{n.AdapterId:N}:{n.Kind}:{n.SsidHex ?? "?"}")
            .Order(StringComparer.Ordinal)) + $"|vpn={VpnDetected}|ok={Reliable}")));
}
