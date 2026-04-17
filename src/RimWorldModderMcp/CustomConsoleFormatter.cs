using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace RimWorldModderMcp;

public sealed class CustomConsoleFormatterOptions : ConsoleFormatterOptions
{
    public LoggerColorBehavior ColorBehavior { get; set; } = LoggerColorBehavior.Enabled;
}

public sealed class CustomConsoleFormatter : ConsoleFormatter, IDisposable
{
    private readonly IDisposable? _optionsReloadToken;
    private CustomConsoleFormatterOptions _formatterOptions;

    private const string LoglevelPadding = ": ";
    private static readonly string _messagePadding = new(' ', GetLogLevelString(LogLevel.Information).Length + LoglevelPadding.Length);
    private static readonly string _newLineWithMessagePadding = Environment.NewLine + _messagePadding;

    public CustomConsoleFormatter(IOptionsMonitor<CustomConsoleFormatterOptions> options)
        : base("custom")
    {
        _optionsReloadToken = options.OnChange(ReloadLoggerOptions);
        _formatterOptions = options.CurrentValue;
    }

    private void ReloadLoggerOptions(CustomConsoleFormatterOptions options)
    {
        _formatterOptions = options;
    }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (logEntry.Exception == null && message == null)
        {
            return;
        }

        var logLevel = logEntry.LogLevel;
        var logLevelColors = GetLogLevelConsoleColors(logLevel);
        var logLevelString = GetLogLevelString(logLevel);

        string? timestamp = null;
        var timestampFormat = _formatterOptions.TimestampFormat;
        if (timestampFormat != null)
        {
            var dateTimeOffset = GetCurrentDateTime();
            timestamp = dateTimeOffset.ToString(timestampFormat);
        }

        if (timestamp != null)
        {
            textWriter.Write(timestamp);
        }

        if (logLevelString != null)
        {
            textWriter.WriteColoredMessage(logLevelString, logLevelColors.Background, logLevelColors.Foreground);
            textWriter.Write(LoglevelPadding);
        }

        // Extract and format category name
        var categoryName = logEntry.Category;
        if (categoryName.StartsWith("RimWorldModderMcp."))
        {
            categoryName = categoryName.Substring("RimWorldModderMcp.".Length);
        }
        
        // Further simplify common patterns
        if (categoryName.StartsWith("Services."))
        {
            categoryName = categoryName.Substring("Services.".Length);
        }

        textWriter.Write($"[{categoryName}] ");

        WriteMessage(textWriter, message, logEntry.Exception?.ToString());
    }

    private void WriteMessage(TextWriter textWriter, string message, string? exceptionText)
    {
        if (!string.IsNullOrEmpty(message))
        {
            textWriter.WriteLine(message);
        }

        if (!string.IsNullOrEmpty(exceptionText))
        {
            textWriter.WriteLine(exceptionText);
        }
    }

    private DateTimeOffset GetCurrentDateTime()
    {
        return _formatterOptions.UseUtcTimestamp ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
    }

    private static string GetLogLevelString(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => throw new ArgumentOutOfRangeException(nameof(logLevel))
        };
    }

    private ConsoleColors GetLogLevelConsoleColors(LogLevel logLevel)
    {
        var disableColors = (_formatterOptions.ColorBehavior == LoggerColorBehavior.Disabled) ||
                            (_formatterOptions.ColorBehavior == LoggerColorBehavior.Default && Console.IsOutputRedirected);
        if (disableColors)
        {
            return new ConsoleColors(null, null);
        }
        // We must explicitly set the background color if we are setting the foreground color,
        // since just setting one can look bad on the users console.
        return logLevel switch
        {
            LogLevel.Trace => new ConsoleColors(ConsoleColor.Gray, ConsoleColor.Black),
            LogLevel.Debug => new ConsoleColors(ConsoleColor.Gray, ConsoleColor.Black),
            LogLevel.Information => new ConsoleColors(ConsoleColor.DarkGreen, ConsoleColor.Black),
            LogLevel.Warning => new ConsoleColors(ConsoleColor.Yellow, ConsoleColor.Black),
            LogLevel.Error => new ConsoleColors(ConsoleColor.Black, ConsoleColor.DarkRed),
            LogLevel.Critical => new ConsoleColors(ConsoleColor.White, ConsoleColor.DarkRed),
            _ => new ConsoleColors(null, null)
        };
    }

    private readonly struct ConsoleColors
    {
        public ConsoleColors(ConsoleColor? foreground, ConsoleColor? background)
        {
            Foreground = foreground;
            Background = background;
        }

        public ConsoleColor? Foreground { get; }

        public ConsoleColor? Background { get; }
    }

    public void Dispose()
    {
        _optionsReloadToken?.Dispose();
    }
}

internal static class TextWriterExtensions
{
    public static void WriteColoredMessage(this TextWriter textWriter, string message, ConsoleColor? background, ConsoleColor? foreground)
    {
        // Order: backgroundcolor, foregroundcolor, Message, reset foregroundcolor, reset backgroundcolor
        if (background.HasValue)
        {
            textWriter.Write($"\x1B[{GetBackgroundColorEscapeCode(background.Value)}m");
        }
        if (foreground.HasValue)
        {
            textWriter.Write($"\x1B[{GetForegroundColorEscapeCode(foreground.Value)}m");
        }

        textWriter.Write(message);
        if (foreground.HasValue)
        {
            textWriter.Write("\x1B[39m"); // reset to default foreground color
        }
        if (background.HasValue)
        {
            textWriter.Write("\x1B[49m"); // reset to the background color
        }
    }

    private static string GetForegroundColorEscapeCode(ConsoleColor color) => color switch
    {
        ConsoleColor.Black => "30",
        ConsoleColor.DarkRed => "31",
        ConsoleColor.DarkGreen => "32",
        ConsoleColor.DarkYellow => "33",
        ConsoleColor.DarkBlue => "34",
        ConsoleColor.DarkMagenta => "35",
        ConsoleColor.DarkCyan => "36",
        ConsoleColor.Gray => "37",
        ConsoleColor.Red => "1;31",
        ConsoleColor.Green => "1;32",
        ConsoleColor.Yellow => "1;33",
        ConsoleColor.Blue => "1;34",
        ConsoleColor.Magenta => "1;35",
        ConsoleColor.Cyan => "1;36",
        ConsoleColor.White => "1;37",
        _ => "39"
    };

    private static string GetBackgroundColorEscapeCode(ConsoleColor color) => color switch
    {
        ConsoleColor.Black => "40",
        ConsoleColor.DarkRed => "41",
        ConsoleColor.DarkGreen => "42",
        ConsoleColor.DarkYellow => "43",
        ConsoleColor.DarkBlue => "44",
        ConsoleColor.DarkMagenta => "45",
        ConsoleColor.DarkCyan => "46",
        ConsoleColor.Gray => "47",
        ConsoleColor.Red => "1;41",
        ConsoleColor.Green => "1;42",
        ConsoleColor.Yellow => "1;43",
        ConsoleColor.Blue => "1;44",
        ConsoleColor.Magenta => "1;45",
        ConsoleColor.Cyan => "1;46",
        ConsoleColor.White => "1;47",
        _ => "49"
    };
}