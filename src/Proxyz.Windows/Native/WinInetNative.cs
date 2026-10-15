using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Proxyz.Windows.Native;

internal static class WinInetNative
{
    internal const uint PerConnectionOption = 75;
    internal const uint SettingsChanged = 39;
    internal const uint Refresh = 37;

    [StructLayout(LayoutKind.Explicit)]
    internal struct OptionValue
    {
        [FieldOffset(0)] internal uint Number;
        [FieldOffset(0)] internal IntPtr String;
        [FieldOffset(0)] internal FILETIME Time;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PerConnectionOptionValue
    {
        internal uint Option;
        internal OptionValue Value;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PerConnectionOptionList
    {
        internal uint Size;
        internal IntPtr Connection;
        internal uint Count;
        internal uint Error;
        internal IntPtr Options;
    }
    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Query(IntPtr internet, uint option, ref PerConnectionOptionList buffer, ref uint size);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Set(IntPtr internet, uint option, ref PerConnectionOptionList buffer, uint size);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Notify(IntPtr internet, uint option, IntPtr buffer, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalFree(IntPtr memory);
}
