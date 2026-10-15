using System.Runtime.InteropServices;
using Proxyz.Windows;
using Proxyz.Windows.Native;

namespace Proxyz.Windows.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Run on Windows."); return 2; }
            Equal(8, Marshal.SizeOf<WinInetNative.OptionValue>(), "option union");
            Equal(IntPtr.Size == 8 ? 16 : 12, Marshal.SizeOf<WinInetNative.PerConnectionOptionValue>(), "per-connection option");
            Equal(IntPtr.Size == 8 ? 32 : 20, Marshal.SizeOf<WinInetNative.PerConnectionOptionList>(), "option list");
            Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf<WinInetNative.PerConnectionOptionValue>("Value").ToInt32(), "option value offset");
            Equal(532, Marshal.SizeOf<WlanNative.InterfaceInfo>(), "WLAN_INTERFACE_INFO");
            Equal(36, Marshal.SizeOf<WlanNative.Ssid>(), "DOT11_SSID");
            Equal(68, Marshal.SizeOf<WlanNative.Association>(), "WLAN_ASSOCIATION_ATTRIBUTES");
            Equal(604, Marshal.SizeOf<WlanNative.ConnectionAttributes>(), "WLAN_CONNECTION_ATTRIBUTES");
            Equal(IntPtr.Size == 8 ? 24 : 12, Marshal.SizeOf<ConnectionDiagnostics.ProxyInfo>(), "WINHTTP_PROXY_INFO");
            Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf<ConnectionDiagnostics.ProxyInfo>("Proxy").ToInt32(), "WINHTTP proxy pointer offset");
            foreach (var (dll, names) in new[]
            {
                ("winhttp.dll", new[] { "WinHttpGetDefaultProxyConfiguration", "WinHttpOpen", "WinHttpConnect", "WinHttpOpenRequest", "WinHttpSetTimeouts", "WinHttpSetOption", "WinHttpSendRequest", "WinHttpReceiveResponse", "WinHttpQueryHeaders", "WinHttpCloseHandle" }),
                ("wininet.dll", new[] { "InternetOpenW", "InternetConnectW", "HttpOpenRequestW", "InternetSetOptionW", "HttpSendRequestW", "HttpQueryInfoW", "InternetCloseHandle" })
            })
            {
                var library = NativeLibrary.Load(dll);
                try { foreach (var name in names) if (!NativeLibrary.TryGetExport(library, name, out _)) throw new Exception("Missing diagnostic API: " + name); }
                finally { NativeLibrary.Free(library); }
            }
            Console.WriteLine("PASS native structure layout checks");
            Console.WriteLine("PASS diagnostic API exports (not invoked; no network requests)");
            if (args.Contains("--read-os", StringComparer.Ordinal))
            {
                var backend = new WinInetProxyBackend();
                var before = backend.Read(); var policy = backend.ReadPolicy();
                if (!ConnectionDiagnostics.Check(null).Success) throw new Exception("Read-only WinINet/WinHTTP diagnostic settings check failed. Configuration values are not printed.");
                Console.WriteLine("PASS read-only WinINet/WinHTTP diagnostics; no URL supplied, no HTTP probes");
                Console.WriteLine($"READ ONLY: flags={before.Flags}, policyCanChange={policy.CanChange}. Addresses are redacted.");
                using var wifi = new WifiObserver(); // consent=false: no WLAN location-sensitive calls
                var snapshot = new NetworkReader(wifi).Read(false);
                if (!snapshot.Reliable) throw new Exception("NLM could not enumerate: " + snapshot.Notice);
                Console.WriteLine($"READ ONLY: {snapshot.Networks.Count} networks; names are redacted.");
                if (!before.SameAs(backend.Read())) throw new Exception("Settings changed externally during the read-only test. Investigate before continuing.");
                Console.WriteLine("PASS read-only native smoke checks");
            }
            else Console.WriteLine("Native reads skipped. Use --read-os to opt in. No writes are implemented in this test executable.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static void Equal(int expected, int actual, string name)
    { if (expected != actual) throw new Exception($"{name}: expected {expected}, actual {actual}"); }
}
