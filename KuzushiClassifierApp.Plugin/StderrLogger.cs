using Microsoft.Extensions.Logging;

namespace KuzushiClassifierApp.Plugin;

/// <summary>
/// stderr-only logging. The plugin's stdout carries JSON-RPC exclusively, so
/// no console provider may ever be registered.
/// </summary>
public sealed class StderrLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class StderrLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

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

            var line = $"[{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += $" ({exception.GetType().Name}: {exception.Message})";
            }

            Console.Error.WriteLine(line);
        }
    }
}
