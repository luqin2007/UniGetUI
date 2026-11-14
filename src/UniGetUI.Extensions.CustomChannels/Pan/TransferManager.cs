using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Pan;

/// <summary>
/// 转存清单与清理策略执行者。所有记录持久化到程序目录 state/transfer-ledger.json，
/// 便于失败重试与「一键清理遗留转存」。
/// </summary>
public sealed class TransferManager
{
    private readonly AppPaths _paths;
    private readonly TransferConfig _config;
    private readonly ILogger<TransferManager> _logger;
    private readonly object _gate = new();
    private TransferLedgerDocument _ledger;

    public TransferManager(AppPaths paths, TransferConfig config, ILogger<TransferManager> logger)
    {
        _paths = paths;
        _config = config;
        _logger = logger;
        _ledger = Load();
    }

    public string LedgerPath => Path.Combine(_paths.State, "transfer-ledger.json");

    public IReadOnlyList<TransferRecord> Entries
    {
        get
        {
            lock (_gate)
            {
                return _ledger.Entries.ToList();
            }
        }
    }

    public TransferCleanupPolicy ResolvePolicy(SourceConfig? source) =>
        source?.CleanupPolicy ?? _config.CleanupPolicy;

    /// <summary>同平台同名同大小且未删除的转存文件可直接复用，避免重复转存。</summary>
    public TransferRecord? FindReusable(string platform, string fileName, long size)
    {
        lock (_gate)
        {
            return _ledger.Entries.FirstOrDefault(e =>
                e.Platform == platform &&
                e.State is not TransferState.Deleted &&
                string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase) &&
                (size <= 0 || e.Size == size));
        }
    }

    public TransferRecord AddOrUpdate(TransferRecord record)
    {
        lock (_gate)
        {
            var existing = _ledger.Entries.FirstOrDefault(e =>
                e.Platform == record.Platform &&
                e.FileId == record.FileId &&
                !string.IsNullOrEmpty(record.FileId));

            if (existing is not null)
            {
                existing.State = record.State;
                existing.Path = record.Path;
                existing.Size = record.Size;
                existing.LastError = null;
                SaveUnsafe();
                return existing;
            }

            _ledger.Entries.Add(record);
            SaveUnsafe();
            return record;
        }
    }

    public void MarkDownloadCompleted(string platform, string fileName)
    {
        lock (_gate)
        {
            foreach (var entry in _ledger.Entries.Where(e =>
                         e.Platform == platform &&
                         string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase) &&
                         e.State is TransferState.Active or TransferState.Reused))
            {
                entry.State = TransferState.DownloadCompleted;
            }

            SaveUnsafe();
        }
    }

    /// <summary>按策略清理指定平台的转存记录。</summary>
    public async Task<int> CleanupAsync(
        IPanTransferService service,
        TransferCleanupPolicy policy,
        bool installSucceeded,
        CancellationToken ct = default)
    {
        if (policy == TransferCleanupPolicy.Keep)
        {
            return 0;
        }

        List<TransferRecord> targets;
        lock (_gate)
        {
            targets = _ledger.Entries
                .Where(e => e.Platform == service.Platform)
                .Where(e => e.State is TransferState.Active or TransferState.DownloadCompleted or TransferState.DeleteFailed)
                .Where(e => policy == TransferCleanupPolicy.DeleteAfterDownload ||
                            (policy == TransferCleanupPolicy.DeleteAfterSuccess && installSucceeded))
                .ToList();
        }

        var deleted = 0;
        foreach (var record in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ok = await service.DeleteAsync(record, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    record.State = ok ? TransferState.Deleted : TransferState.DeleteFailed;
                    if (!ok)
                    {
                        record.LastError = "删除未成功";
                    }
                }

                if (ok)
                {
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_gate)
                {
                    record.State = TransferState.DeleteFailed;
                    record.LastError = ex.Message;
                }

                _logger.LogWarning("清理转存文件失败：{Name} - {Message}", record.FileName, ex.Message);
            }
        }

        lock (_gate)
        {
            SaveUnsafe();
        }

        return deleted;
    }

    /// <summary>重试所有删除失败的记录。</summary>
    public async Task<int> RetryFailedAsync(IPanTransferService service, CancellationToken ct = default)
    {
        List<TransferRecord> failed;
        lock (_gate)
        {
            failed = _ledger.Entries
                .Where(e => e.Platform == service.Platform && e.State == TransferState.DeleteFailed)
                .ToList();
        }

        var count = 0;
        foreach (var record in failed)
        {
            try
            {
                if (await service.DeleteAsync(record, ct).ConfigureAwait(false))
                {
                    record.State = TransferState.Deleted;
                    record.LastError = null;
                    count++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                record.LastError = ex.Message;
            }
        }

        lock (_gate)
        {
            SaveUnsafe();
        }

        return count;
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            _ledger.Entries.RemoveAll(e => e.State is TransferState.Deleted);
            SaveUnsafe();
        }
    }

    private TransferLedgerDocument Load()
    {
        try
        {
            return JsonFileStore.Read(LedgerPath, AppJsonContext.Default.TransferLedgerDocument)
                   ?? new TransferLedgerDocument();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.LogWarning("转存清单读取失败，将重建：{Message}", ex.Message);
            return new TransferLedgerDocument();
        }
    }

    private void SaveUnsafe() => JsonFileStore.Write(LedgerPath, _ledger, AppJsonContext.Default.TransferLedgerDocument);
}
