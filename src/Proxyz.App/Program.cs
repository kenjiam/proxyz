using System.Diagnostics;
using System.Runtime.InteropServices;
using Proxyz.App.Infrastructure;
using Proxyz.Cli;

namespace Proxyz.App;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException("Windows 10 / 11 が必要です。");
            bool demo = args.Contains("--demo"); bool yes = args.Contains("--yes"); bool dryRun = args.Contains("--dry-run");
            bool idle = args.Contains("--idle");
            bool host = args.Contains("--host");
            var command = args.Where(a => a is not ("--demo" or "--yes" or "--dry-run" or "--idle" or "--host")).ToArray();
            if (command.Length == 0) command = ["--help"];
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), demo ? "Proxyz-Demo" : "Proxyz");
            if (command.SequenceEqual(["--background"])) command = ["tray"]; // previous startup registration
            if (command.SequenceEqual(["tray"]))
            {
                if (dryRun) { Console.WriteLine("トレイ常駐を起動します。" + (demo ? " DEMO" : "")); return 0; }
                using var existing = LocalProtocol.Connect(demo).GetAwaiter().GetResult();
                if (existing is not null) { Console.WriteLine("Proxyz は既に常駐しています。"); return 0; }
                if (!host)
                {
                    StartTray(demo, idle);
                    for (int i = 0; i < 20; i++)
                    {
                        using var started = LocalProtocol.Connect(demo, 250).GetAwaiter().GetResult();
                        if (started is not null) { Console.WriteLine("Proxyz トレイを起動しました。"); return 0; }
                    }
                    throw new IOException("トレイに接続できません。別セッションの常駐やデータの利用状況を確認してください。");
                }
                using var fileLock = LocalProtocol.Acquire(directory);
                FreeConsole();
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
                using var tray = new TrayContext(directory, demo, idle);
                System.Windows.Forms.Application.Run(tray);
                return 0;
            }
            if (idle || host) throw new ArgumentException("--idle / --host are only valid with tray.");
            // Help/version never open user data, query the network, or start a host.
            if (command.SequenceEqual(["--help"]) || command.SequenceEqual(["version"]))
            { Console.WriteLine(command[0] == "version" ? "Proxyz 0.1.0 — CLI + tray" : CommandService.Help); return 0; }
            var pipe = LocalProtocol.Connect(demo).GetAwaiter().GetResult();
            if (pipe is null && command.SequenceEqual(["up"]) && !dryRun)
            {
                StartTray(demo, true);
                for (int i = 0; i < 20 && pipe is null; i++) pipe = LocalProtocol.Connect(demo, 250).GetAwaiter().GetResult();
                if (pipe is null) throw new IOException("トレイに接続できません。proxyz tray を実行して確認してください。");
            }
            if (pipe is not null) return LocalProtocol.Client(pipe, command, dryRun, preview => Confirm(preview, yes)).GetAwaiter().GetResult();
            if (command.SequenceEqual(["quit"])) { Console.WriteLine("Proxyz は常駐していません。Windows 設定は維持しています。"); return 0; }
            using var standaloneLock = LocalProtocol.Acquire(directory);
            using var runtime = new AppRuntime(directory, demo);
            var platform = new RuntimePlatform(runtime, false, () => { });
            var service = new CommandService(runtime.Controller, platform);
            using var plan = service.Prepare(command);
            if (dryRun) { Console.WriteLine(plan.Preview); return 0; }
            if (plan.RequiresConfirmation && !Confirm(plan.Preview, yes)) return 2;
            var result = plan.Execute(); Console.WriteLine(result.Text); return result.ExitCode;
        }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 1; }
        catch (FormatException e) { Console.Error.WriteLine(e.Message); return 1; }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 4; }
    }
    private static bool Confirm(string preview, bool yes)
    {
        Console.WriteLine(preview);
        if (yes) return true;
        if (Console.IsInputRedirected) { Console.Error.WriteLine("非対話実行には --yes が必要です。変更していません。"); return false; }
        Console.Write("実行しますか？ [y/N]: ");
        return string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase);
    }
    private static void StartTray(bool demo, bool idle)
    {
        var path = Environment.ProcessPath ?? throw new IOException("Missing executable path.");
        // Shell launch does not inherit a redirected CLI stdout/stderr pipe. With
        // inherited handles PowerShell would wait for the resident host to exit.
        var start = new ProcessStartInfo(path) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("tray"); start.ArgumentList.Add("--host"); if (idle) start.ArgumentList.Add("--idle"); if (demo) start.ArgumentList.Add("--demo");
        using var process = Process.Start(start) ?? throw new IOException("トレイを起動できません。");
    }
}
