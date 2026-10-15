using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Proxyz.Windows;

/// <summary>Read-only settings plus explicit, bounded native HTTP probes. Never sets OS proxy settings.</summary>
public static class ConnectionDiagnostics
{
    public sealed record Report(bool Success, string Text);
    public static Report Check(Uri? target)
    {
        var output = new List<string>();
        bool success = true;
        try
        {
            var settings = new WinInetProxyBackend().Read();
            output.Add($"WinINet: flags={settings.Flags}\nServer: {settings.ProxyServer}\nBypass: {settings.ProxyBypass}\nPAC: {settings.AutoConfigUrl}");
        }
        catch (Exception e) { success = false; output.Add("WinINet read: " + e.Message); }
        try
        {
            var settings = ReadDefault();
            output.Add($"WinHTTP static default: access={settings.Access}\nServer: {settings.Proxy}\nBypass: {settings.Bypass}");
        }
        catch (Exception e) { success = false; output.Add("WinHTTP read: " + e.Message); }
        if (target is null) output.Add("通信テストは未実施。proxyz check --url <URL> で接続先を明示してください。");
        else
        {
            if (target.Scheme is not ("http" or "https") || target.UserInfo.Length != 0) throw new ArgumentException("HTTP / HTTPS URL without credentials required.");
            foreach (var (name, probe) in new (string, Func<uint>)[]
            {
                ("WinINet current-user", () => ProbeWinInet(target)),
                ("WinHTTP static default", () => ProbeWinHttp(target, false)),
                ("WinHTTP automatic (system + current-user)", () => ProbeWinHttp(target, true))
            })
            {
                try
                {
                    uint status = probe();
                    success &= status is >= 200 and < 400;
                    output.Add($"{name}: HTTP {status}" + (status == 407 ? " — proxy authentication required" : status == 401 ? " — server authentication required" : status is >= 300 and < 400 ? " — redirect not followed" : ""));
                }
                catch (Exception e) { success = false; output.Add($"{name}: FAILED — {e.Message}"); }
            }
            output.Add("HEAD response only. No automatic authentication, cookies, redirects, certificate exceptions, or Proxyz direct fallback. OS PAC/WPAD resolution may contact additional servers. HTTP status is not a guarantee for other apps.");
        }
        return new(success, string.Join("\n\n", output));
    }
    private sealed record DefaultProxy(uint Access, string Proxy, string Bypass);
    private static DefaultProxy ReadDefault()
    {
        var native = new ProxyInfo();
        try
        {
            Require(WinHttpGetDefaultProxyConfiguration(out native));
            return new(native.Access, Marshal.PtrToStringUni(native.Proxy) ?? "", Marshal.PtrToStringUni(native.Bypass) ?? "");
        }
        finally { if (native.Proxy != IntPtr.Zero) GlobalFree(native.Proxy); if (native.Bypass != IntPtr.Zero) GlobalFree(native.Bypass); }
    }
    private static uint ProbeWinInet(Uri uri)
    {
        using var session = InternetOpen("Proxyz diagnostic", 0, null, null, 0); RequireHandle(session);
        uint timeout = 5000;
        foreach (uint option in new uint[] { 2, 5, 6 }) Require(InternetSetOption(session, option, ref timeout, 4));
        using var connection = InternetConnect(session, uri.IdnHost, (ushort)uri.Port, null, null, 3, 0, IntPtr.Zero); RequireHandle(connection);
        // Reload, no cache writes, no cookies/auth/UI/redirects, optional TLS.
        uint flags = 0x80000000 | 0x04000000 | 0x00080000 | 0x00040000 | 0x00000200 | 0x00200000;
        if (uri.Scheme == "https") flags |= 0x00800000;
        using var request = HttpOpenRequest(connection, "HEAD", uri.PathAndQuery, null, null, IntPtr.Zero, flags, IntPtr.Zero); RequireHandle(request);
        Require(HttpSendRequest(request, null, 0, IntPtr.Zero, 0));
        uint status = 0, length = 4, index = 0;
        Require(HttpQueryInfo(request, 19 | 0x20000000, ref status, ref length, ref index)); return status;
    }
    private static uint ProbeWinHttp(Uri uri, bool automatic)
    {
        var config = automatic ? new DefaultProxy(4, "", "") : ReadDefault();
        using var session = WinHttpOpen("Proxyz diagnostic", config.Access, config.Access == 3 ? config.Proxy : null, config.Access == 3 ? config.Bypass : null, 0); RequireHandle(session);
        Require(WinHttpSetTimeouts(session, 5000, 5000, 5000, 5000));
        using var connection = WinHttpConnect(session, uri.IdnHost, (ushort)uri.Port, 0); RequireHandle(connection);
        using var request = WinHttpOpenRequest(connection, "HEAD", uri.PathAndQuery, null, null, IntPtr.Zero, uri.Scheme == "https" ? 0x00800000u : 0); RequireHandle(request);
        uint neverAutoLogon = 2; Require(WinHttpSetOption(request, 77, ref neverAutoLogon, 4));
        uint disable = 1 | 2 | 4; // cookies, redirects, authentication
        Require(WinHttpSetOption(request, 63, ref disable, 4));
        Require(WinHttpSendRequest(request, null, 0, IntPtr.Zero, 0, 0, IntPtr.Zero));
        Require(WinHttpReceiveResponse(request, IntPtr.Zero));
        uint status = 0, length = 4, index = 0;
        Require(WinHttpQueryHeaders(request, 19 | 0x20000000, null, ref status, ref length, ref index)); return status;
    }
    private static void Require(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private static void RequireHandle(SafeHandle handle) { if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] internal struct ProxyInfo { public uint Access; public IntPtr Proxy; public IntPtr Bypass; }
    private sealed class InetHandle : SafeHandleZeroOrMinusOneIsInvalid
    { public InetHandle() : base(true) { } protected override bool ReleaseHandle() => InternetCloseHandle(handle); }
    private sealed class HttpHandle : SafeHandleZeroOrMinusOneIsInvalid
    { public HttpHandle() : base(true) { } protected override bool ReleaseHandle() => WinHttpCloseHandle(handle); }
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpGetDefaultProxyConfiguration(out ProxyInfo info);
    [DllImport("wininet.dll", EntryPoint = "InternetOpenW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern InetHandle InternetOpen(string agent, uint access, string? proxy, string? bypass, uint flags);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InternetSetOption(InetHandle handle, uint option, ref uint value, uint length);
    [DllImport("wininet.dll", EntryPoint = "InternetConnectW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern InetHandle InternetConnect(InetHandle session, string host, ushort port, string? user, string? password, uint service, uint flags, IntPtr context);
    [DllImport("wininet.dll", EntryPoint = "HttpOpenRequestW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern InetHandle HttpOpenRequest(InetHandle connection, string verb, string path, string? version, string? referrer, IntPtr accept, uint flags, IntPtr context);
    [DllImport("wininet.dll", EntryPoint = "HttpSendRequestW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HttpSendRequest(InetHandle request, string? headers, uint headerLength, IntPtr data, uint length);
    [DllImport("wininet.dll", EntryPoint = "HttpQueryInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HttpQueryInfo(InetHandle request, uint info, ref uint value, ref uint length, ref uint index);
    [DllImport("wininet.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InternetCloseHandle(IntPtr handle);
    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern HttpHandle WinHttpOpen(string agent, uint access, string? proxy, string? bypass, uint flags);
    [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpSetTimeouts(HttpHandle session, int resolve, int connect, int send, int receive);
    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern HttpHandle WinHttpConnect(HttpHandle session, string host, ushort port, uint reserved);
    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern HttpHandle WinHttpOpenRequest(HttpHandle connection, string verb, string path, string? version, string? referrer, IntPtr accept, uint flags);
    [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpSetOption(HttpHandle handle, uint option, ref uint value, uint length);
    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpSendRequest(HttpHandle request, string? headers, uint headerLength, IntPtr data, uint length, uint total, IntPtr context);
    [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpReceiveResponse(HttpHandle request, IntPtr reserved);
    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpQueryHeaders(HttpHandle request, uint info, string? name, ref uint value, ref uint length, ref uint index);
    [DllImport("winhttp.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpCloseHandle(IntPtr handle);
}
