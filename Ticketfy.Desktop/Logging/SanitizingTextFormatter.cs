using Serilog.Events;
using Serilog.Formatting;

namespace Ticketfy.Logging;

public sealed class SanitizingTextFormatter : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        string message = LogSanitizer.Sanitize(logEvent.RenderMessage());
        output.Write($"{logEvent.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level(logEvent.Level)}] {message}");
        if (logEvent.Exception != null)
            output.Write($"{Environment.NewLine}{LogSanitizer.Sanitize(logEvent.Exception.ToString())}");
        output.WriteLine();
    }

    private static string Level(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "UNK"
    };
}
