using System.Text.Json;
using System.Text.Json.Serialization;
using Proxyz.Core.Models;

namespace Proxyz.Core.Storage;

/// <summary>One process owns the directory. A corrupt or newer file is never reset silently.</summary>
public sealed class JsonStateStore(string directory) : IStateStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24,
        Converters = { new JsonStringEnumConverter() }
    };

    public AppConfig LoadConfig()
    {
        var value = Read<AppConfig>("config.json") ?? new();
        Validation.Config(value);
        return value;
    }
    public void SaveConfig(AppConfig config)
    {
        Validation.Config(config);
        AtomicWrite("config.json", config);
    }
    public ChangeJournal LoadJournal()
    {
        var journal = Read<ChangeJournal>("journal.json") ?? new();
        if (journal.SchemaVersion != 1) throw new InvalidDataException("復元記録の形式を読み込めません。自動変更を開始しません。");
        if (journal.Pending is not null && (journal.Pending.Before is null || journal.Pending.Intended is null))
            throw new InvalidDataException("未完了の復元記録が破損しています。上書きしません。");
        // Recovery data is deliberately not subject to new-profile validation, so
        // pre-existing Windows settings outside the editor's grammar can be restored.
        foreach (var s in new[] { journal.Original, journal.Previous, journal.LastApplied, journal.Pending?.Before, journal.Pending?.Intended, journal.Pending?.RecoveryTarget })
            if (s is not null && (s.ProxyServer is null || s.ProxyBypass is null || s.AutoConfigUrl is null))
                throw new InvalidDataException("復元記録が破損しています。上書きしません。");
        return journal;
    }
    public void SaveJournal(ChangeJournal journal) => AtomicWrite("journal.json", journal);

    private T? Read<T>(string name) where T : class
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException($"{name} が大きすぎます。");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? throw new InvalidDataException($"{name} が空です。");
        }
        catch (JsonException e) { throw new InvalidDataException($"{name} を読み込めません。ファイルを保存したまま、修復を行ってください。", e); }
    }

    private void AtomicWrite<T>(string name, T data)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, data, JsonOptions);
                if (stream.Length > 2 * 1024 * 1024)
                    throw new InvalidDataException("設定ファイルが2 MBを超えます。保存済みのファイルは変更していません。");
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: false);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
