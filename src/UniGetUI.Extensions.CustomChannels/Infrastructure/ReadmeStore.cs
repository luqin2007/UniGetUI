using System.Text.Json;

namespace UniGetUI.Extensions.CustomChannels.Infrastructure;

public sealed record ReadmeIndexEntry(string Sha256, DateTimeOffset CheckedAt);

/// <summary>
/// 记录每个「目标 + 渠道 + 版本」README 的内容哈希。
/// 版本检查时比对，用于判断说明文档是否随版本更新。
/// </summary>
public sealed class ReadmeStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<string, ReadmeIndexEntry>? _entries;

    public ReadmeStore(AppPaths paths) => _path = Path.Combine(paths.State, "readme-index.json");

    /// <summary>记录哈希，并返回与上次记录相比内容是否发生变化（首次记录返回 false）。</summary>
    public bool TryUpdate(string key, string sha256, DateTimeOffset checkedAt)
    {
        lock (_gate)
        {
            var entries = Load();
            var changed = entries.TryGetValue(key, out var existing) &&
                          !string.Equals(existing.Sha256, sha256, StringComparison.OrdinalIgnoreCase);

            entries[key] = new ReadmeIndexEntry(sha256, checkedAt);
            Save(entries);
            return changed;
        }
    }

    /// <summary>读取已知哈希（无记录时返回 null）。</summary>
    public string? GetHash(string key)
    {
        lock (_gate)
        {
            return Load().TryGetValue(key, out var entry) ? entry.Sha256 : null;
        }
    }

    private Dictionary<string, ReadmeIndexEntry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        try
        {
            if (File.Exists(_path))
            {
                _entries = JsonSerializer.Deserialize<Dictionary<string, ReadmeIndexEntry>>(
                    File.ReadAllText(_path), SerializerOptions);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _entries = null;
        }

        return _entries ??= new Dictionary<string, ReadmeIndexEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save(Dictionary<string, ReadmeIndexEntry> entries)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, SerializerOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 记录失败不影响版本检查结果
        }
    }
}
