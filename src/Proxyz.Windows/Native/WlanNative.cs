using System.Runtime.InteropServices;

namespace Proxyz.Windows.Native;

internal static class WlanNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct InterfaceInfo
    {
        internal Guid Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Description;
        internal uint State;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Ssid
    {
        internal uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] Bytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Association
    {
        internal Ssid Ssid;
        internal uint BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] internal byte[] Bssid;
        internal uint PhyType;
        internal uint PhyIndex;
        internal uint SignalQuality;
        internal uint RxRate;
        internal uint TxRate;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Security
    {
        internal int SecurityEnabled;
        internal int OneXEnabled;
        internal uint AuthAlgorithm;
        internal uint CipherAlgorithm;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ConnectionAttributes
    {
        internal uint State;
        internal uint Mode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string ProfileName;
        internal Association Association;
        internal Security Security;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct NotificationData
    {
        internal uint Source;
        internal uint Code;
        internal Guid InterfaceId;
        internal uint DataSize;
        internal IntPtr Data;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void NotificationCallback(ref NotificationData data, IntPtr context);
    [DllImport("wlanapi.dll")]
    internal static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")]
    internal static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")]
    internal static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")]
    internal static extern uint WlanQueryInterface(IntPtr handle, ref Guid id, uint opcode, IntPtr reserved, out uint size, out IntPtr data, out uint opcodeType);
    [DllImport("wlanapi.dll")]
    internal static extern void WlanFreeMemory(IntPtr memory);
    [DllImport("wlanapi.dll")]
    internal static extern uint WlanRegisterNotification(IntPtr handle, uint source, [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicate,
        NotificationCallback? callback, IntPtr context, IntPtr reserved, out uint previousSource);
}
