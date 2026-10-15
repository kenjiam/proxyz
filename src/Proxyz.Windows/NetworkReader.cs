using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Proxyz.Core.Models;
using Proxyz.Windows.Native;

namespace Proxyz.Windows;

public sealed class NetworkReader(WifiObserver wifi)
{
    public NetworkSnapshot Read(bool observeWifi)
    {
        var wlan = wifi.Read(observeWifi);
        var networks = new List<NetworkIdentity>();
        var cards = new Dictionary<Guid, NetworkInterface>();
        bool vpn = false;
        object? manager = null;
        IEnumNetworksNative? enumeration = null;
        try
        {
            foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (Guid.TryParse(card.Id, out var id)) cards[id] = card;
                if (card.OperationalStatus == OperationalStatus.Up) vpn |= LooksLikeTunnel(card);
            }
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), true)!;
            manager = Activator.CreateInstance(type)!;
            enumeration = ((INetworkListManagerNative)manager).GetNetworks(1);
            int count = 0;
            while (true)
            {
                int hr = enumeration.Next(1, out var network, out var fetched);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                if (fetched == 0 || network is null) { Release(network); break; }
                if (++count > 512) { Release(network); throw new IOException("ネットワークの列挙件数が上限を超えました。"); }
                IEnumConnectionsNative? connections = null;
                try
                {
                    Guid networkId = network.GetNetworkId();
                    string name = network.GetName();
                    connections = network.GetNetworkConnections();
                    int connectionCount = 0;
                    while (true)
                    {
                        int connectionHr = connections.Next(1, out var connection, out var connectionFetched);
                        if (connectionHr < 0) Marshal.ThrowExceptionForHR(connectionHr);
                        if (connectionFetched == 0 || connection is null) { Release(connection); break; }
                        try
                        {
                            if (++connectionCount > 512) throw new IOException("接続の列挙件数が上限を超えました。");
                            if (!connection.GetIsConnected()) continue;
                            Guid adapter = connection.GetAdapterId();
                            cards.TryGetValue(adapter, out var card);
                            var kind = card?.NetworkInterfaceType switch
                            {
                                NetworkInterfaceType.Wireless80211 => NetworkKind.WiFi,
                                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or
                                NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT => NetworkKind.Ethernet,
                                _ => NetworkKind.Other
                            };
                            wlan.Interfaces.TryGetValue(adapter, out var ssid);
                            networks.Add(new() { NetworkId = networkId, AdapterId = adapter, Name = name,
                                Kind = kind, SsidHex = ssid?.Hex, SsidLabel = ssid?.Label });
                        }
                        finally { Release(connection); }
                    }
                }
                finally { Release(connections); Release(network); }
            }
            return new()
            {
                Networks = networks.DistinctBy(n => (n.NetworkId, n.AdapterId)).ToList(), Reliable = true,
                VpnDetected = vpn, Notice = networks.Any(n => n.Kind == NetworkKind.WiFi) ? wlan.Notice : ""
            };
        }
        catch (Exception e)
        {
            return new() { Networks = networks, Reliable = false, VpnDetected = vpn,
                Notice = "Windows のネットワーク識別情報を取得できません。自動切替は行いません。詳細: " + e.GetType().Name };
        }
        finally { Release(enumeration); Release(manager); }
    }
    private static bool LooksLikeTunnel(NetworkInterface card)
    {
        if (card.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) return true;
        string text = card.Name + " " + card.Description;
        return new[] { "WireGuard", "Wintun", "Tailscale", "TAP-Windows", "ZeroTier", "VPN" }.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase));
    }
    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
