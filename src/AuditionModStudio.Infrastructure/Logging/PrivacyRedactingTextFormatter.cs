using System.Globalization;
using AuditionModStudio.Core.Diagnostics;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace AuditionModStudio.Infrastructure.Logging;

public sealed class PrivacyRedactingTextFormatter(
    string outputTemplate,
    ISensitiveDataRedactor redactor) : ITextFormatter
{
    private readonly MessageTemplateTextFormatter _inner = new(outputTemplate, CultureInfo.InvariantCulture);

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);
        foreach (var property in logEvent.Properties.ToArray())
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key,
                RedactValue(property.Key, property.Value)));
        }
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        _inner.Format(logEvent, buffer);
        output.Write(redactor.Redact(buffer.ToString()));
    }

    private LogEventPropertyValue RedactValue(string name, LogEventPropertyValue value)
    {
        if (redactor.IsSensitiveName(name)) return new ScalarValue("[REDACTED]");
        return value switch
        {
            ScalarValue { Value: string text } => new ScalarValue(redactor.Redact(text)),
            SequenceValue sequence => new SequenceValue(sequence.Elements.Select(element =>
                RedactValue(name, element))),
            StructureValue structure => new StructureValue(structure.Properties.Select(property =>
                new LogEventProperty(property.Name, RedactValue(property.Name, property.Value))),
                structure.TypeTag),
            DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(pair =>
                new KeyValuePair<ScalarValue, LogEventPropertyValue>(pair.Key,
                    RedactValue(pair.Key.Value?.ToString() ?? name, pair.Value)))),
            _ => value,
        };
    }
}
