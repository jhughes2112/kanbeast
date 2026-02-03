using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace KanBeast.Server;

public sealed class MinimalConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "minimal";

    public MinimalConsoleFormatter() : base(FormatterName)
    {
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        string? message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);

        if (message == null)
        {
            return;
        }

        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        textWriter.WriteLine($"{timestamp} {message}");

        if (logEntry.Exception != null)
        {
            textWriter.WriteLine(logEntry.Exception.ToString());
        }
    }
}
