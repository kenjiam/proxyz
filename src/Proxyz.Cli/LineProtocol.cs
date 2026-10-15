using System.Text;

namespace Proxyz.Cli;

public static class LineProtocol
{
    public static async Task<string?> ReadLine(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0)
        {
            if (buffer[0] == '\n') return text.ToString().TrimEnd('\r');
            if (text.Length >= 1024 * 1024) throw new InvalidDataException("Local message exceeds 1 MiB.");
            text.Append(buffer[0]);
        }
        return text.Length == 0 ? null : throw new EndOfStreamException("Incomplete local message.");
    }
}
