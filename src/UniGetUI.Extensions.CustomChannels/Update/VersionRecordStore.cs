using System.Text.Json;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Update;

/// <summary>一次版本记录：更新成功后写入的远端版本。</summary>
public sealed record VersionRecord(
    string TargetId,
    string Version,
    string? SourceId,
    DateTimeOffset RecordedAt,
    string? Note = null);

/// <summary>
/// 记录目标当前的版本（写入程序目录 state/versions/{targetId}.json）。
/// 适用于无法从文件或命令读取版本的程序：每次更新成功后自动记录远端版本，
/// 首次使用前可由用户在界面填写初始值。
/// </summary>
public sealed class VersionRecordStore(AppPaths paths, ILogger<VersionRecordStore> logger)
{
    public string GetRecordPath(string targetId) =>
        Path.Combine(paths.State, "versions", Sanitize(targetId) + ".json");

    public VersionRecord? Read(string targetId)
    {
        var path = GetRecordPath(targetId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonFileStore.Read(path, AppJsonContext.Default.VersionRecord);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("读取版本记录失败：{Path} - {Message}", path, ex.Message);
            return null;
        }
    }

    public VersionRecord Save(string targetId, string version, string? sourceId, string? note = null)
    {
        var record = new VersionRecord(targetId, version, sourceId, DateTimeOffset.Now, note);
        try
        {
            JsonFileStore.Write(GetRecordPath(targetId), record, AppJsonContext.Default.VersionRecord);
            logger.LogInformation(
                "已记录版本：{Target} → {Version}（来源：{Source}）", targetId, version, sourceId ?? "未知");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("写入版本记录失败：{Target} - {Message}", targetId, ex.Message);
        }

        return record;
    }

    public bool Delete(string targetId)
    {
        var path = GetRecordPath(targetId);
        if (!File.Exists(path))
        {
            return false;
        }

        JsonFileStore.TryDelete(path);
        return !File.Exists(path);
    }

    private static string Sanitize(string targetId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = targetId.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var name = new string(chars).Trim();
        return string.IsNullOrEmpty(name) ? "target" : name;
    }
}
