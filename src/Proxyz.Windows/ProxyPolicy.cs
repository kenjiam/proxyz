using Microsoft.Win32;
using Proxyz.Core.Models;

namespace Proxyz.Windows;

public static class ProxyPolicy
{
    public static PolicyStatus Read()
    {
        try
        {
            const string internet = @"Software\Policies\Microsoft\Windows\CurrentVersion\Internet Settings";
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
            using (var key = machine.OpenSubKey(internet))
            {
                if (key?.GetValue("ProxySettingsPerUser") is int value && value == 0)
                    return new(false, "組織がプロキシをコンピューター単位で管理しています。Proxyz はユーザー設定を上書きしません。");
            }
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var control = root.OpenSubKey(@"Software\Policies\Microsoft\Internet Explorer\Control Panel");
                if (Nonzero(control, "Proxy") || Nonzero(control, "AutoConfig"))
                    return new(false, "組織のポリシーでプロキシ設定の変更が制限されています。ネットワーク管理者へ確認してください。");
                using var values = root.OpenSubKey(internet);
                if (new[] { "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL" }.Any(name => values?.GetValue(name) is not null))
                    return new(false, "組織のポリシーにプロキシ設定が指定されています。Proxyz から上書きしません。");
            }
            bool browserManaged = false;
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                foreach (var browser in new[] { @"Software\Policies\Microsoft\Edge", @"Software\Policies\Google\Chrome" })
                {
                    using var key = root.OpenSubKey(browser);
                    browserManaged |= new[] { "ProxyMode", "ProxySettings", "ProxyServer", "ProxyPacUrl" }.Any(name => key?.GetValue(name) is not null);
                }
            }
            return new(true, "", browserManaged
                ? "ブラウザー独自の管理ポリシーがあります。Windows 設定を適用しても、そのブラウザーには反映されない場合があります。" : "");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new(false, "組織の管理制限を確認できないため、プロキシ設定を変更しません。");
        }
    }
    private static bool Nonzero(RegistryKey? key, string name) => key?.GetValue(name) is int value && value != 0;
}
