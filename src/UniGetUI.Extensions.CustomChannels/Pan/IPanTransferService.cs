namespace UniGetUI.Extensions.CustomChannels.Pan;

/// <summary>网盘转存能力：分享文件 → 当前账号，并支持删除已转存文件。</summary>
public interface IPanTransferService
{
    string Platform { get; }

    Task<TransferResult> TransferAsync(TransferRequest request, CancellationToken ct = default);

    Task<bool> DeleteAsync(TransferRecord record, CancellationToken ct = default);
}

public sealed class PanApiException(string message, string? rawResponse = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string? RawResponse { get; } = rawResponse;
}
