using UniGetUI.Extensions.CustomChannels.Infrastructure;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Packages;

/// <summary>一次单文件安装的结果：安装目录内的文件名与创建的快捷方式。</summary>
public sealed record InstalledFileRecord(
    string TargetId,
    string? FileName,
    List<string> ShortcutPaths,
    DateTimeOffset InstalledAt);

/// <summary>
/// 记录单文件应用当前安装的文件名与快捷方式（state/installed/{targetId}.json）。
/// 文件名通常含版本号，更新后需要据此清理上一版文件与失效快捷方式。
/// </summary>
public sealed class InstalledFileStore(AppPaths paths, ILogger<InstalledFileStore> logger)
{
    public string GetRecordPath(string targetId) =>
        Path.Combine(paths.State, "installed", AppPaths.Sanitize(targetId) + ".json");

    public InstalledFileRecord? Read(string targetId)
    {
        var path = GetRecordPath(targetId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonFileStore.Read(path, AppJsonContext.Default.InstalledFileRecord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            logger.LogWarning("读取安装记录失败：{Path} - {Message}", path, ex.Message);
            return null;
        }
    }

    public void Save(InstalledFileRecord record)
    {
        try
        {
            JsonFileStore.Write(GetRecordPath(record.TargetId), record, AppJsonContext.Default.InstalledFileRecord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("写入安装记录失败：{Target} - {Message}", record.TargetId, ex.Message);
        }
    }

    public void Delete(string targetId) => JsonFileStore.TryDelete(GetRecordPath(targetId));
}
