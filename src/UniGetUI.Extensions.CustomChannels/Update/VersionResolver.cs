using System.Diagnostics;
using System.Text.RegularExpressions;
using UniGetUI.Extensions.CustomChannels.Configuration;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Update;

/// <summary>
/// 读取本地已安装版本。支持可执行文件版本信息、版本文件、
/// 本工具记录的版本（更新后自动写入），以及执行命令解析输出。
/// </summary>
public sealed partial class VersionResolver(
    VersionRecordStore records,
    ILogger<VersionResolver> logger)
{
    [GeneratedRegex(@"\d+(?:\.\d+){1,3}", RegexOptions.Compiled)]
    private static partial Regex VersionRegex();

    public string? GetLocalVersion(TargetConfig target)
    {
        var config = target.Version;
        return config.Mode switch
        {
            LocalVersionMode.None => null,
            LocalVersionMode.Recorded => GetRecordedVersion(target),
            LocalVersionMode.Command => GetCommandVersion(target),
            _ => GetFileVersion(target),
        };
    }

    private string? GetRecordedVersion(TargetConfig target)
    {
        var record = records.Read(target.Id);
        if (record is null || string.IsNullOrWhiteSpace(record.Version))
        {
            logger.LogWarning(
                "目标 {Target} 尚无版本记录，请在目标编辑页填写初始版本（更新成功后会自动记录）。",
                target.Id);
            return null;
        }

        return Normalize(record.Version);
    }

    private string? GetCommandVersion(TargetConfig target)
    {
        var config = target.Version;
        var command = config.Command;
        if (string.IsNullOrWhiteSpace(command))
        {
            logger.LogWarning("目标 {Target} 选择了命令获取版本，但未配置命令。", target.Id);
            return null;
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(config.CommandTimeoutSeconds, 3, 120));

        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);

            if (!string.IsNullOrWhiteSpace(target.Install.RootPath) &&
                Directory.Exists(target.Install.RootPath))
            {
                startInfo.WorkingDirectory = target.Install.RootPath;
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                logger.LogWarning("无法启动版本命令：{Command}", command);
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                }

                logger.LogWarning("版本命令超时（{Seconds} 秒）：{Command}", timeout.TotalSeconds, command);
                return null;
            }

            var text = stdoutTask.GetAwaiter().GetResult() + "\n" + stderrTask.GetAwaiter().GetResult();
            var version = ExtractVersion(text, config.Pattern);
            if (version is null)
            {
                logger.LogWarning("版本命令没有输出可识别的版本号：{Command}", command);
            }

            return version;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException
                                       or IOException)
        {
            logger.LogWarning("执行版本命令失败：{Command} - {Message}", command, ex.Message);
            return null;
        }
    }

    private string? GetFileVersion(TargetConfig target)
    {
        var config = target.Version;
        var relative = config.Path ?? target.Install.ExePath;
        if (string.IsNullOrWhiteSpace(relative))
        {
            logger.LogWarning("目标 {Target} 未配置版本文件路径，无法读取本地版本。", target.Id);
            return null;
        }

        var root = target.Install.RootPath;
        var path = Path.IsPathFullyQualified(relative) ? relative : Path.Combine(root, relative);

        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("版本来源文件不存在：{Path}", path);
                return null;
            }

            if (config.Mode is LocalVersionMode.FileVersion or LocalVersionMode.ProductVersion)
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var value = config.Mode == LocalVersionMode.FileVersion
                    ? info.FileVersion ?? info.ProductVersion
                    : info.ProductVersion ?? info.FileVersion;

                return Normalize(value);
            }

            return ExtractVersion(File.ReadAllText(path), config.Pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning("读取本地版本失败：{Path} - {Message}", path, ex.Message);
            return null;
        }
    }

    private static string? ExtractVersion(string? text, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(pattern))
        {
            var match = Regex.Match(text, pattern);
            return match.Success
                ? Normalize(match.Groups.Count > 1 ? match.Groups[1].Value : match.Value)
                : null;
        }

        return Normalize(text);
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = VersionRegex().Match(value);
        return match.Success ? match.Value : value.Trim();
    }
}
