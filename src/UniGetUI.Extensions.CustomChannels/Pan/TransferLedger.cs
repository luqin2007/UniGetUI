namespace UniGetUI.Extensions.CustomChannels.Pan;

public enum TransferState
{
    /// <summary>已转存，等待下载。</summary>
    Active,

    /// <summary>下载完成，等待按策略清理。</summary>
    DownloadCompleted,

    /// <summary>已删除。</summary>
    Deleted,

    /// <summary>删除失败，需重试。</summary>
    DeleteFailed,

    /// <summary>复用了此前已转存的文件。</summary>
    Reused,
}

public sealed class TransferRecord
{
    public string Platform { get; set; } = string.Empty;

    public string ShareId { get; set; } = string.Empty;

    public string FileId { get; set; } = string.Empty;

    public string? FileToken { get; set; }

    public string? Path { get; set; }

    public string FileName { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTimeOffset TransferredAt { get; set; } = DateTimeOffset.UtcNow;

    public TransferState State { get; set; } = TransferState.Active;

    public string? LastError { get; set; }
}

public sealed class TransferLedgerDocument
{
    public List<TransferRecord> Entries { get; set; } = new();
}

/// <summary>转存请求描述。</summary>
public sealed record TransferRequest(
    string Platform,
    string ShareId,
    string ShareUrl,
    string? Pwd,
    string FileId,
    string? FileToken,
    string FileName,
    long Size,
    string TransferPath);

/// <summary>转存结果。</summary>
public sealed record TransferResult(
    bool Success,
    string? FileId,
    string? FilePath,
    string? Message,
    bool Reused = false);
