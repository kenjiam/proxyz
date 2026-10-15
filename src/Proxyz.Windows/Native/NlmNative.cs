using System.Runtime.InteropServices;

namespace Proxyz.Windows.Native;

// Vtable prefixes of the documented dual interfaces in netlistmgr.h.
// Deliberately use typed COM calls: GUID-valued methods do not require a
// typelib-dependent dynamic binder, and enumeration does not rely on IEnumerable.
[ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetworkListManagerNative
{
    [DispId(1)] IEnumNetworksNative GetNetworks(uint flags);
}

[ComImport, Guid("DCB00003-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IEnumNetworksNative
{
    [DispId(-4)] [return: MarshalAs(UnmanagedType.IUnknown)] object GetNewEnum();
    // Only celt=1 is used; rgelt is therefore a single interface pointer.
    [DispId(1), PreserveSig]
    int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out INetworkNative? network, out uint fetched);
}

[ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetworkNative
{
    [DispId(1)] [return: MarshalAs(UnmanagedType.BStr)] string GetName();
    [DispId(2)] void SetName([MarshalAs(UnmanagedType.BStr)] string name);
    [DispId(3)] [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
    [DispId(4)] void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
    [DispId(5)] Guid GetNetworkId();
    [DispId(6)] uint GetDomainType();
    [DispId(7)] IEnumConnectionsNative GetNetworkConnections();
}

[ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IEnumConnectionsNative
{
    [DispId(-4)] [return: MarshalAs(UnmanagedType.IUnknown)] object GetNewEnum();
    [DispId(1), PreserveSig]
    int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out INetworkConnectionNative? connection, out uint fetched);
}

[ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetworkConnectionNative
{
    [DispId(1)] INetworkNative GetNetwork();
    [DispId(2)] [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnectedToInternet();
    [DispId(3)] [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnected();
    [DispId(4)] uint GetConnectivity();
    [DispId(5)] Guid GetConnectionId();
    [DispId(6)] Guid GetAdapterId();
}
