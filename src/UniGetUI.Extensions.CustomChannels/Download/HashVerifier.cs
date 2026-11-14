using System.Security.Cryptography;

namespace UniGetUI.Extensions.CustomChannels.Download;

/// <summary>流式 SHA256 与文件大小校验。</summary>
public static class HashVerifier
{
    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task<bool> VerifyAsync(
        string path,
        string? expectedSha256,
        long? expectedSize,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var info = new FileInfo(path);
        if (expectedSize is { } size && size > 0 && info.Length != size)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return true;
        }

        var actual = await ComputeSha256Async(path, ct).ConfigureAwait(false);
        return string.Equals(actual.Trim(), expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
