using Serilog.Events;
using Serilog.Formatting;

namespace Ticketfy.Logging;

public sealed class SanitizingTextFormatter : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        var safeEvent = new LogEvent(logEvent.Timestamp, logEvent.Level, null, logEvent.MessageTemplate,
            logEvent.Properties.Select(p => new LogEventProperty(p.Key,
                SanitizeProperty(p.Key, p.Value))));
        string message = LogSanitizer.Sanitize(safeEvent.RenderMessage());
        output.Write($"{logEvent.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level(logEvent.Level)}] {message}");
        if (logEvent.Exception != null)
        {
            // Exception messages can contain raw notes, SQL parameters or personal data.
            // Type, code and stack retain diagnostic locations without exporting those values.
            Exception? exception = logEvent.Exception;
            for (int depth = 0; exception is not null && depth < 5; depth++, exception = exception.InnerException)
                output.Write($"{Environment.NewLine}{exception.GetType().FullName} (0x{exception.HResult:X8}){Environment.NewLine}{LogSanitizer.Sanitize(exception.StackTrace)}");
        }
        output.WriteLine();
    }

    private static LogEventPropertyValue SanitizeProperty(string name, LogEventPropertyValue value)
    {
        if (LogSanitizer.IsSensitiveProperty(name)) return new ScalarValue("[REDACTED]");
        return value switch
        {
            StructureValue structure => new StructureValue(structure.Properties.Select(p =>
                new LogEventProperty(p.Name, SanitizeProperty(p.Name, p.Value))), structure.TypeTag),
            SequenceValue sequence => new SequenceValue(sequence.Elements.Select(v => SanitizeProperty("", v))),
            DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(p =>
                new KeyValuePair<ScalarValue, LogEventPropertyValue>(p.Key, SanitizeProperty(p.Key.Value?.ToString() ?? "", p.Value)))),
            ScalarValue { Value: string text } => new ScalarValue(LogSanitizer.Sanitize(text)),
            _ => value
        };
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
