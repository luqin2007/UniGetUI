using System.Text.RegularExpressions;
using UniGetUI.Extensions.CustomChannels.Configuration;

namespace UniGetUI.Extensions.CustomChannels.Sources;

/// <summary>
/// 根据 Release 资产名生成文件名通配符：把版本片段替换为 *，
/// 让同一仓库的后续版本也能自动匹配。
/// </summary>
public static partial class AssetPatternSuggester
{
    [GeneratedRegex(@"\d+(?:\.\d+){1,3}", RegexOptions.Compiled)]
    private static partial Regex VersionSegmentRegex();

    [GeneratedRegex(@"\*+", RegexOptions.Compiled)]
    private static partial Regex RepeatedStarRegex();

    /// <summary>从资产列表中挑选最贴合安装方式的一条并生成通配符。</summary>
    public static string Suggest(IReadOnlyList<string> assetNames, InstallKind kind)
    {
        if (assetNames.Count == 0)
        {
            return string.Empty;
        }

        var extensions = kind switch
        {
            // Msi/Exe 安装程序：优先 msi，其次 exe 安装包
            InstallKind.Msi => new[] { ".msi", ".msix", ".exe" },
            InstallKind.Exe or InstallKind.SingleFile => [".exe"],
            _ => [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz"],
        };

        var matching = assetNames
            .Where(name => extensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matching.Count == 0)
        {
            matching = assetNames.ToList();
        }

        var preferred = matching.FirstOrDefault(IsX64) ?? matching[0];
        return ToPattern(preferred);
    }

    /// <summary>把单个资产名转为通配符，例如 ExHyperV_V1.5.2_Setup_x64.exe → ExHyperV_V*_Setup_x64.exe。</summary>
    public static string ToPattern(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var pattern = VersionSegmentRegex().Replace(fileName.Trim(), "*");
        pattern = RepeatedStarRegex().Replace(pattern, "*");
        return pattern;
    }

    private static bool IsX64(string name) =>
        name.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("amd64", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("win64", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("x86_64", StringComparison.OrdinalIgnoreCase);
}
