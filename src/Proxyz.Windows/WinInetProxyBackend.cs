using System.ComponentModel;
using System.Runtime.InteropServices;
using Proxyz.Core;
using Proxyz.Core.Models;
using Proxyz.Windows.Native;

namespace Proxyz.Windows;

/// <summary>
/// Only the current user's default LAN/Internet settings. Never run as SYSTEM.
/// lpszConnection = null deliberately leaves RAS/VPN-specific settings untouched.
/// Strings returned by WinINet must be freed with GlobalFree, not FreeHGlobal.
/// </summary>
public sealed class WinInetProxyBackend : IProxyBackend
{
    public ProxySettings Read()
    {
        try { return ReadOptions(10); } // INTERNET_PER_CONN_FLAGS_UI on Windows 7+
        catch (Win32Exception) { return ReadOptions(1); }
    }
    public PolicyStatus ReadPolicy() => ProxyPolicy.Read();

    public void Write(ProxySettings settings)
    {
        var policy = ReadPolicy();
        if (!policy.CanChange) throw new InvalidOperationException(policy.Reason);
        int stride = Marshal.SizeOf<WinInetNative.PerConnectionOptionValue>();
        IntPtr options = Marshal.AllocHGlobal(stride * 4);
        var strings = new List<IntPtr>();
        try
        {
            WriteSlot(options, stride, 0, 1, new() { Number = settings.Flags });
            string[] values = [settings.ProxyServer, settings.ProxyBypass, settings.AutoConfigUrl];
            for (int i = 0; i < values.Length; i++)
            {
                var p = Marshal.StringToHGlobalUni(values[i]); strings.Add(p);
                WriteSlot(options, stride, i + 1, (uint)(i + 2), new() { String = p });
            }
            var list = MakeList(options);
            if (!WinInetNative.Set(IntPtr.Zero, WinInetNative.PerConnectionOption, ref list, list.Size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows のユーザープロキシ設定を変更できませんでした。");
            if (!WinInetNative.Notify(IntPtr.Zero, WinInetNative.SettingsChanged, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows にプロキシ変更を通知できませんでした。");
            if (!WinInetNative.Notify(IntPtr.Zero, WinInetNative.Refresh, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "プロキシ設定の再読み込みを要求できませんでした。");
        }
        finally
        {
            foreach (var p in strings) Marshal.FreeHGlobal(p);
            Marshal.FreeHGlobal(options);
        }
    }

    private static ProxySettings ReadOptions(uint flagsOption)
    {
        int stride = Marshal.SizeOf<WinInetNative.PerConnectionOptionValue>();
        IntPtr options = Marshal.AllocHGlobal(stride * 4);
        for (int i = 0; i < 4; i++) WriteSlot(options, stride, i, i == 0 ? flagsOption : (uint)(i + 1), default);
        try
        {
            var list = MakeList(options);
            uint size = list.Size;
            if (!WinInetNative.Query(IntPtr.Zero, WinInetNative.PerConnectionOption, ref list, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "現在の Windows プロキシ設定を取得できませんでした。");
            return new()
            {
                Flags = ReadSlot(options, stride, 0).Value.Number,
                ProxyServer = ReadString(options, stride, 1),
                ProxyBypass = ReadString(options, stride, 2),
                AutoConfigUrl = ReadString(options, stride, 3)
            };
        }
        finally
        {
            for (int i = 1; i < 4; i++)
            {
                var p = ReadSlot(options, stride, i).Value.String;
                if (p != IntPtr.Zero) WinInetNative.GlobalFree(p);
            }
            Marshal.FreeHGlobal(options);
        }
    }
    private static WinInetNative.PerConnectionOptionList MakeList(IntPtr options) => new()
    {
        Size = (uint)Marshal.SizeOf<WinInetNative.PerConnectionOptionList>(),
        Connection = IntPtr.Zero, Count = 4, Error = 0, Options = options
    };
    private static void WriteSlot(IntPtr p, int stride, int index, uint option, WinInetNative.OptionValue value) =>
        Marshal.StructureToPtr(new WinInetNative.PerConnectionOptionValue { Option = option, Value = value }, IntPtr.Add(p, stride * index), false);
    private static WinInetNative.PerConnectionOptionValue ReadSlot(IntPtr p, int stride, int index) =>
        Marshal.PtrToStructure<WinInetNative.PerConnectionOptionValue>(IntPtr.Add(p, stride * index));
    private static string ReadString(IntPtr p, int stride, int index) =>
        Marshal.PtrToStringUni(ReadSlot(p, stride, index).Value.String) ?? "";
}
