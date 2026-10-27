using Serilog;
using Ticketfy.Core.Helpers;
using Ticketfy.Logging;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseTimeAndPrivacyTests
{
    [Theory]
    [InlineData("2026-01-10T05:30:00+00:00", "2026-01-09 23:30:00")]
    [InlineData("2026-08-10T05:30:00Z", "2026-08-09 23:30:00")]
    [InlineData("2026-10-18 12:30:00", "2026-10-18 12:30:00")]
    [InlineData("2026-10-18T12:30:00-07:00", "2026-10-18 13:30:00")]
    [InlineData("10/11/2026 13:30:00", "2026-11-10 13:30:00")]
    public void BusinessCalendar_DoesNotDependOnWindowsZoneAndPreservesLegacyWallClock(string raw, string expected)
    {
        Assert.True(BusinessTime.TryLocal(raw, out var actual, BusinessTime.Resolve("America/Mexico_City")));
        Assert.Equal(expected, actual.ToString("yyyy-MM-dd HH:mm:ss"));
        Assert.Equal(DateTimeKind.Unspecified, actual.Kind);
    }

    [Fact]
    public void BusinessCalendar_UsesExplicitZonesAndRejectsAmbiguousLocalInstants()
    {
        var utc = new DateTime(2026, 1, 10, 5, 30, 0, DateTimeKind.Utc);
        var mexico = BusinessTime.Resolve("America/Mexico_City");
        var cancun = BusinessTime.Resolve("America/Cancun");
        Assert.True(BusinessTime.TryLocal(utc.ToString("O"), out var mexicoDate, mexico));
        Assert.True(BusinessTime.TryLocal(utc.ToString("O"), out var cancunDate, cancun));
        Assert.NotEqual(mexicoDate.Date, cancunDate.Date);
        Assert.Equal(utc, BusinessTime.ToUtc(mexicoDate, mexico));
        Assert.Equal(utc, BusinessTime.ToUtc(cancunDate, cancun));
        var border = BusinessTime.Resolve("America/Tijuana");
        Assert.Throws<InvalidOperationException>(() => BusinessTime.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0), border));
        Assert.Throws<InvalidOperationException>(() => BusinessTime.ToUtc(new DateTime(2026, 11, 1, 1, 30, 0), border));
        Assert.False(BusinessTime.TryLocal("not a date", out _));
        Assert.Throws<TimeZoneNotFoundException>(() => BusinessTime.Resolve("Invalid/Business"));
    }

    [Fact]
    public void BusinessRange_IncludesTheLastInstantButExcludesNextBusinessDay()
    {
        DateTime day = new(2026, 10, 18);
        Assert.True("2026-10-19T05:59:59.9999999Z".IsInDateRange(day, day));
        Assert.False("2026-10-19T06:00:00Z".IsInDateRange(day, day));
        Assert.True("2026-10-18 23:59:59".IsInDateRange(day, day));
    }

    [Fact]
    public void Logs_RedactStructuredPersonalFieldsAndDoNotExportFreeTextExceptions()
    {
        using var output = new StringWriter();
        using var logger = new LoggerConfiguration().WriteTo.Sink(new FormatterSink(output)).CreateLogger();
        logger.Information("Operator {FullName}, address {Address}, note {NoteText}, phone {Phone}, count {Count}",
            "PERSONAL NAME", "PRIVATE STREET 123", "PRIVATE NOTE", "5512345678", 7);
        logger.Error(new InvalidOperationException("SECRET PERSONAL DETAILS"), "Operation failed");
        logger.Information("Payload {@Payload}", new { Customer = new { FullName = "NESTED PERSON" }, Items = new[] { new { Address = "NESTED ADDRESS", Quantity = 2 } }, Tags = new Dictionary<string, string> { ["Phone"] = "5512345678" } });
        string text = output.ToString();
        foreach (var sensitive in new[] { "PERSONAL NAME", "PRIVATE STREET", "PRIVATE NOTE", "5512345678", "SECRET PERSONAL DETAILS", "NESTED PERSON", "NESTED ADDRESS" })
            Assert.DoesNotContain(sensitive, text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("count 7", text);
        Assert.Contains("[REDACTED]", text);
    }

    [Theory]
    [InlineData("Contacto 5512345678")]
    [InlineData("Contacto +52 55 1234 5678")]
    [InlineData("Contacto (55) 1234 5678")]
    public void Logs_RedactMexicanTenDigitPhones(string input)
    {
        string safe = LogSanitizer.Sanitize(input);
        Assert.Contains("[PHONE-REDACTED]", safe);
    }

    private sealed class FormatterSink(TextWriter output) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent) => new SanitizingTextFormatter().Format(logEvent, output);
    }
}
