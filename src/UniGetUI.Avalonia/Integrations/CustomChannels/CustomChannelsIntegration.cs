using Microsoft.Extensions.Logging;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.Logging;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Services;

namespace UniGetUI.Avalonia.Integrations.CustomChannels;

/// <summary>
/// [CustomChannels] 自定义渠道扩展的入口：构造页面 ViewModel 与后台服务，
/// 并把状态文本暴露给「设置 &gt; 扩展」列表。
/// </summary>
internal static class CustomChannelsIntegration
{
    private static CustomChannelsViewModel? _current;

    internal static AppExtensionSession Start(MainWindowViewModel window)
    {
        _ = window;
        var viewModel = new CustomChannelsViewModel(CreateLoggerFactory());
        var previous = Interlocked.Exchange(ref _current, viewModel);
        previous?.Stop();

        return new AppExtensionSession(
            () => new CustomChannelsPage(viewModel),
            () =>
            {
                Interlocked.CompareExchange(ref _current, null, viewModel);
                viewModel.Stop();
            },
            viewModel.CheckForUpdatesAsync);
    }

    internal static string Status() => _current?.StatusSummary ?? string.Empty;

    /// <summary>活动中的页面 ViewModel（设置页保存后用于热重载配置）。</summary>
    internal static CustomChannelsViewModel? Current => _current;

    /// <summary>通过该工厂创建的服务与适配器都会转发到 UniGetUI 的日志系统（含脱敏）。</summary>
    internal static ILoggerFactory CreateLoggerFactory() =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new UniGetUILoggerProvider());
        });
}

/// <summary>把 Microsoft.Extensions.Logging 的日志转发到 UniGetUI 的滚动日志。</summary>
internal sealed class UniGetUILoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new UniGetUILogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class UniGetUILogger(string category) : ILogger
    {
        private readonly string _short = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

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

            var message = $"[CustomChannels] {_short}: {formatter(state, exception)}";
            if (exception is not null)
            {
                message += Environment.NewLine + exception;
            }

            switch (logLevel)
            {
                case LogLevel.Error:
                case LogLevel.Critical:
                    Logger.Error(message);
                    break;
                case LogLevel.Warning:
                    Logger.Warn(message);
                    break;
                case LogLevel.Debug:
                case LogLevel.Trace:
                    Logger.Debug(message);
                    break;
                default:
                    Logger.Info(message);
                    break;
            }
        }
    }
}
