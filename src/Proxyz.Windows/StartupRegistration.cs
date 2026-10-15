using Microsoft.Win32;

namespace Proxyz.Windows;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool IsRegistered()
        => ReadCommand() is not null;
    public static string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("Proxyz") as string;
    }
    public static void Set(bool enabled)
    {
        string path = Environment.ProcessPath ?? throw new InvalidOperationException("実行ファイルの場所を取得できません。");
        if (enabled && !string.Equals(Path.GetFileName(path), "Proxyz.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("発行済みの Proxyz.exe から自動起動を登録してください。");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key is null) throw new IOException("自動起動の登録先を開けません。");
        if (!enabled) { key.DeleteValue("Proxyz", throwOnMissingValue: false); return; }
        key.SetValue("Proxyz", "\"" + path + "\" tray --host", RegistryValueKind.String);
    }
}
