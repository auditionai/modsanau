using System.Text;

namespace AuditionModStudio.Archives;

internal enum AcvTool5ParsedEventKind
{
    KeydatMissing,
    CountrySelectionRequested,
    ExtractItem,
    PackItem,
    KnownError,
}

internal sealed record AcvTool5ParsedEvent(AcvTool5ParsedEventKind Kind, string? Value = null);

internal sealed class AcvTool5OutputParser
{
    internal const int MaximumBufferedCharacters = 16_384;

    private const string KeydatMissingText = "Keydat file not found";
    private const string SelectPrompt = "Select:";
    private const int MaximumProtocolTokenLength = 21;
    private readonly StringBuilder _lineBuffer = new();
    private string _scanTail = string.Empty;
    private bool _keydatMissingReported;
    private bool _selectionReported;

    internal int BufferedCharacterCount => _lineBuffer.Length + _scanTail.Length;

    public IReadOnlyList<AcvTool5ParsedEvent> Feed(ReadOnlySpan<char> chunk)
    {
        var events = new List<AcvTool5ParsedEvent>();
        if (chunk.IsEmpty)
        {
            return events;
        }

        var chunkText = chunk.ToString();
        var scanText = _scanTail + chunkText;
        InspectProtocolTokens(scanText, events);
        var tailLength = Math.Min(MaximumProtocolTokenLength - 1, scanText.Length);
        _scanTail = scanText[^tailLength..];

        _lineBuffer.Append(chunkText);
        DrainCompleteLines(events);
        if (_lineBuffer.Length > MaximumBufferedCharacters)
        {
            _lineBuffer.Remove(0, _lineBuffer.Length - MaximumBufferedCharacters);
        }

        return events;
    }

    public IReadOnlyList<AcvTool5ParsedEvent> Complete()
    {
        var events = new List<AcvTool5ParsedEvent>();
        if (_lineBuffer.Length > 0)
        {
            ParseLine(_lineBuffer.ToString(), events);
            _lineBuffer.Clear();
        }

        _scanTail = string.Empty;
        return events;
    }

    private void InspectProtocolTokens(string text, ICollection<AcvTool5ParsedEvent> events)
    {
        if (!_keydatMissingReported
            && text.Contains(KeydatMissingText, StringComparison.OrdinalIgnoreCase))
        {
            _keydatMissingReported = true;
            events.Add(new(AcvTool5ParsedEventKind.KeydatMissing));
        }

        if (!_selectionReported
            && text.Contains(SelectPrompt, StringComparison.OrdinalIgnoreCase))
        {
            _selectionReported = true;
            events.Add(new(AcvTool5ParsedEventKind.CountrySelectionRequested));
        }
    }

    private void DrainCompleteLines(ICollection<AcvTool5ParsedEvent> events)
    {
        while (true)
        {
            var newlineIndex = IndexOfNewline(_lineBuffer);
            if (newlineIndex < 0)
            {
                return;
            }

            var line = _lineBuffer.ToString(0, newlineIndex).TrimEnd('\r');
            _lineBuffer.Remove(0, newlineIndex + 1);
            ParseLine(line, events);
        }
    }

    private static int IndexOfNewline(StringBuilder value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static void ParseLine(string line, ICollection<AcvTool5ParsedEvent> events)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("writing :", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new(AcvTool5ParsedEventKind.ExtractItem, trimmed["writing :".Length..].Trim()));
        }
        else if (trimmed.StartsWith("Packing:", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new(AcvTool5ParsedEventKind.PackItem, trimmed["Packing:".Length..].Trim()));
        }
        else if ((trimmed.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                  || trimmed.Contains(" failed", StringComparison.OrdinalIgnoreCase))
                 && !trimmed.Contains(KeydatMissingText, StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new(AcvTool5ParsedEventKind.KnownError, trimmed));
        }
    }
}
