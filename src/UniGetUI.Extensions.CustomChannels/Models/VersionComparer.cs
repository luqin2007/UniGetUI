namespace UniGetUI.Extensions.CustomChannels.Models;

/// <summary>
/// 版本号比较：优先按数字段比较（支持 v1.2.3、1.2.3.4、2024.10.1），
/// 预发布后缀按语义化规则排在对应正式版之前，无法解析时退回自然排序。
/// </summary>
public sealed class VersionComparer : IComparer<string?>
{
    public static VersionComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(x))
        {
            return -1;
        }

        if (string.IsNullOrWhiteSpace(y))
        {
            return 1;
        }

        var left = Parse(x);
        var right = Parse(y);

        var max = Math.Max(left.Numbers.Count, right.Numbers.Count);
        for (var i = 0; i < max; i++)
        {
            var a = i < left.Numbers.Count ? left.Numbers[i] : 0;
            var b = i < right.Numbers.Count ? right.Numbers[i] : 0;
            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }

        // 数字段相同：有预发布后缀的更低
        if (left.Prerelease is null && right.Prerelease is null)
        {
            return Math.Sign(string.Compare(x, y, StringComparison.OrdinalIgnoreCase));
        }

        if (left.Prerelease is null)
        {
            return 1;
        }

        if (right.Prerelease is null)
        {
            return -1;
        }

        return Math.Sign(string.Compare(left.Prerelease, right.Prerelease, StringComparison.OrdinalIgnoreCase));
    }

    public static (List<long> Numbers, string? Prerelease) Parse(string value)
    {
        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var numbers = new List<long>();
        string? prerelease = null;

        var segments = text.Split(['-', '+'], 2);
        var numericPart = segments[0];
        if (segments.Length > 1)
        {
            prerelease = segments[1];
        }

        foreach (var chunk in numericPart.Split(['.', '_', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(chunk, out var n))
            {
                numbers.Add(n);
                continue;
            }

            // 处理 "1.2.3beta" 这类混合写法
            var digits = new string(chunk.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length > 0 && long.TryParse(digits, out var prefix))
            {
                numbers.Add(prefix);
                prerelease ??= chunk[digits.Length..];
            }
            else
            {
                prerelease ??= chunk;
            }
        }

        return (numbers, prerelease);
    }

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        return text.Length == 0 ? null : text;
    }
}
