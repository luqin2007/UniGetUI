using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Pan;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace UniGetUI.Extensions.CustomChannels.Tests;

public sealed class TransferManagerTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;

    public TransferManagerTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
    }

    [Fact]
    public void 同名同大小的未删除记录可以复用()
    {
        var manager = CreateManager();
        manager.AddOrUpdate(Record("app.zip", 1024));

        manager.FindReusable("quark", "APP.ZIP", 1024).Should().NotBeNull();
        manager.FindReusable("quark", "app.zip", 2048).Should().BeNull();
        manager.FindReusable("baidu", "app.zip", 1024).Should().BeNull();
    }

    [Fact]
    public void 已删除的记录不再复用()
    {
        var manager = CreateManager();
        var record = manager.AddOrUpdate(Record("app.zip", 1024));
        record.State = TransferState.Deleted;

        manager.FindReusable("quark", "app.zip", 1024).Should().BeNull();
    }

    [Fact]
    public async Task 保留策略不触发任何删除()
    {
        var manager = CreateManager();
        manager.AddOrUpdate(Record("app.zip", 1024));
        var service = new FakeTransferService();

        var deleted = await manager.CleanupAsync(service, TransferCleanupPolicy.Keep, installSucceeded: true);

        deleted.Should().Be(0);
        service.DeleteAttempts.Should().Be(0);
    }

    [Fact]
    public async Task 下载后删除策略应清理活跃与已完成记录()
    {
        var manager = CreateManager();
        manager.AddOrUpdate(Record("a.zip", 1));
        manager.AddOrUpdate(Record("b.zip", 2, TransferState.DownloadCompleted));
        manager.AddOrUpdate(Record("c.zip", 3, TransferState.Deleted));
        var service = new FakeTransferService();

        var deleted = await manager.CleanupAsync(
            service, TransferCleanupPolicy.DeleteAfterDownload, installSucceeded: false);

        deleted.Should().Be(2);
        service.DeleteAttempts.Should().Be(2);
        manager.Entries.Where(e => e.FileName is "a.zip" or "b.zip")
            .Should().OnlyContain(e => e.State == TransferState.Deleted);
    }

    [Fact]
    public async Task 安装成功后才删除策略在失败时保留文件()
    {
        var manager = CreateManager();
        manager.AddOrUpdate(Record("a.zip", 1));
        var service = new FakeTransferService();

        var failedRun = await manager.CleanupAsync(
            service, TransferCleanupPolicy.DeleteAfterSuccess, installSucceeded: false);
        var successRun = await manager.CleanupAsync(
            service, TransferCleanupPolicy.DeleteAfterSuccess, installSucceeded: true);

        failedRun.Should().Be(0);
        successRun.Should().Be(1);
    }

    [Fact]
    public async Task 删除失败应记录状态并支持重试()
    {
        var manager = CreateManager();
        manager.AddOrUpdate(Record("a.zip", 1));
        var service = new FakeTransferService { DeleteResult = _ => false };

        await manager.CleanupAsync(service, TransferCleanupPolicy.DeleteAfterDownload, installSucceeded: true);

        var entry = manager.Entries.Single();
        entry.State.Should().Be(TransferState.DeleteFailed);
        entry.LastError.Should().NotBeNullOrWhiteSpace();

        service.DeleteResult = _ => true;
        var retried = await manager.RetryFailedAsync(service);

        retried.Should().Be(1);
        manager.Entries.Single().State.Should().Be(TransferState.Deleted);
    }

    [Fact]
    public void 转存清单应持久化到状态目录()
    {
        var manager = CreateManager();
        manager.AddOrUpdate(Record("app.zip", 1024));

        File.Exists(Path.Combine(_paths.State, "transfer-ledger.json")).Should().BeTrue();

        var reloaded = CreateManager();
        reloaded.Entries.Should().ContainSingle()
            .Which.FileName.Should().Be("app.zip");
    }

    [Fact]
    public void 清理历史只移除已删除记录()
    {
        var manager = CreateManager();
        var deleted = manager.AddOrUpdate(Record("old.zip", 1));
        deleted.State = TransferState.Deleted;
        manager.AddOrUpdate(Record("keep.zip", 2));

        manager.ClearHistory();

        manager.Entries.Should().ContainSingle().Which.FileName.Should().Be("keep.zip");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试目录清理失败不影响断言结果
        }
    }

    private TransferManager CreateManager(TransferConfig? config = null) =>
        new(_paths, config ?? new TransferConfig(), NullLogger<TransferManager>.Instance);

    private static TransferRecord Record(string fileName, long size, TransferState state = TransferState.Active) => new()
    {
        Platform = "quark",
        ShareId = "share-1",
        FileId = "file-" + fileName,
        FileName = fileName,
        Path = "/AppUpdater/" + fileName,
        Size = size,
        State = state,
    };

    private sealed class FakeTransferService : IPanTransferService
    {
        public string Platform => "quark";

        public int DeleteAttempts { get; private set; }

        public Func<TransferRecord, bool> DeleteResult { get; set; } = _ => true;

        public Task<TransferResult> TransferAsync(TransferRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TransferResult(true, "file-id", request.TransferPath, null));

        public Task<bool> DeleteAsync(TransferRecord record, CancellationToken ct = default)
        {
            DeleteAttempts++;
            return Task.FromResult(DeleteResult(record));
        }
    }
}
