using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Proxyz.Cli;

namespace Proxyz.App;

internal sealed record WireRequest(string[] Args);
internal sealed record WirePreview(string Preview, bool Confirm, int ErrorCode = 0);

internal static class LocalProtocol
{
    public static string PipeName(bool demo)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Missing current-user SID.");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..24];
        return $"Proxyz.Cli.{key}.{Process.GetCurrentProcess().SessionId}" + (demo ? ".Demo" : "");
    }
    public static FileStream Acquire(string directory)
    {
        Directory.CreateDirectory(directory);
        try { return new(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException("Proxyz のデータは他のプロセス / セッションが使用中です。古い GUI や別セッションの常駐を確認してください。", e); }
    }
    public static Task<string?> ReadLine(StreamReader reader, CancellationToken token) => LineProtocol.ReadLine(reader, token);
    public static async Task<NamedPipeClientStream?> Connect(bool demo, int timeout = 250)
    {
        var pipe = new NamedPipeClientStream(".", PipeName(demo), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(timeout); return pipe; }
        catch (TimeoutException) { pipe.Dispose(); return null; }
        catch { pipe.Dispose(); throw; }
    }
    public static async Task<int> Client(NamedPipeClientStream pipe, string[] args, bool dryRun, Func<string, bool> confirm)
    {
        using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new WireRequest(args)));
            var preview = JsonSerializer.Deserialize<WirePreview>(await ReadLine(reader, timeout.Token) ?? throw new EndOfStreamException())!;
            if (preview.ErrorCode != 0) { Console.Error.WriteLine(preview.Preview); return preview.ErrorCode; }
            bool accepted = !dryRun && (!preview.Confirm || confirm(preview.Preview));
            if (dryRun) Console.WriteLine(preview.Preview);
            await writer.WriteLineAsync(accepted ? "execute" : "cancel");
            var result = JsonSerializer.Deserialize<CommandResult>(await ReadLine(reader, timeout.Token) ?? throw new EndOfStreamException())!;
            if (accepted) Console.WriteLine(result.Text);
            return dryRun ? 0 : accepted ? result.ExitCode : 2;
        }
    }
    public static async Task Serve(bool demo, Func<string[], CommandPlan> prepare, Action changed, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(PipeName(demo), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(stop);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop); timeout.CancelAfter(TimeSpan.FromMinutes(3));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                CommandPlan plan;
                try
                {
                    var request = JsonSerializer.Deserialize<WireRequest>(await ReadLine(reader, timeout.Token) ?? throw new EndOfStreamException());
                    if (request?.Args is null || request.Args.Length > 128 || request.Args.Any(a => a is null)) throw new ArgumentException("Invalid local request.");
                    plan = prepare(request.Args);
                }
                catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or IOException or JsonException)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new WirePreview(e.Message, false, 1))); continue;
                }
                using (plan)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new WirePreview(plan.Preview, plan.RequiresConfirmation)));
                    if (await ReadLine(reader, timeout.Token) != "execute") { await writer.WriteLineAsync(JsonSerializer.Serialize(new CommandResult(2, "取消しました。"))); continue; }
                    CommandResult result;
                    try { result = plan.Execute(); }
                    catch (Exception e) { result = new(4, e.Message); }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(result));
                    changed();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException) { /* closed / timed-out client: never execute its preview */ }
        }
    }
}
