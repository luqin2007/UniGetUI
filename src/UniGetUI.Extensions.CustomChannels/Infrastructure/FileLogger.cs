using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Infrastructure;

/// <summary>异步滚动文件日志，写入程序目录 logs，敏感字段统一脱敏。</summary>
public sealed class FileLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    private readonly string _directory;
    private readonly Channel<string> _channel;
    private readonly Task _writerTask;
    private readonly LogLevel _minimumLevel;
    private bool _disposed;

    public FileLoggerProvider(AppPaths paths, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = paths.Logs;
        Directory.CreateDirectory(_directory);
        _minimumLevel = minimumLevel;
        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public string LogDirectory => _directory;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName, _minimumLevel);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
    }

    /// <summary>最近一次日志文件路径，供界面展示。</summary>
    public string GetCurrentLogFile() =>
        Path.Combine(_directory, $"updater-{DateTime.Now:yyyyMMdd}.log");

    internal void Enqueue(string line) => _channel.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        var currentDate = DateTime.MinValue;

        try
        {
            await foreach (var line in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (writer is null || DateTime.Today != currentDate)
                {
                    writer?.Dispose();
                    currentDate = DateTime.Today;
                    var path = Path.Combine(_directory, $"updater-{currentDate:yyyyMMdd}.log");
                    writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
                    {
                        AutoFlush = true,
                    };
                }

                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志写入失败不应影响主流程
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category, LogLevel minimumLevel) : ILogger
    {
        private readonly string _shortCategory = Shorten(category);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = SensitiveDataRedactor.Redact(formatter(state, exception));
            var sb = new StringBuilder(160);
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(LevelText(logLevel)).Append("] ")
                .Append(_shortCategory).Append(": ").Append(message);

            if (exception is not null)
            {
                sb.AppendLine().Append(SensitiveDataRedactor.Redact(exception.ToString()));
            }

            provider.Enqueue(sb.ToString());
        }

        private static string LevelText(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };

        private static string Shorten(string category)
        {
            var index = category.LastIndexOf('.');
            return index >= 0 && index < category.Length - 1 ? category[(index + 1)..] : category;
        }
    }
}

/// <summary>同时在文件与控制台输出的日志器工厂辅助。</summary>
public static class LoggerFactoryBuilder
{
    public static (ILoggerFactory Factory, FileLoggerProvider Provider) Create(AppPaths paths, LogLevel minimumLevel = LogLevel.Information)
    {
        var provider = new FileLoggerProvider(paths, minimumLevel);
        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minimumLevel);
            builder.AddProvider(provider);
        });
        return (factory, provider);
    }
}
