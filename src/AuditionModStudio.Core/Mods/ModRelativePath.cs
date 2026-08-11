namespace AuditionModStudio.Core.Mods;

public readonly record struct ModRelativePath
{
    public const int MaximumLength = 512;

    private static readonly char[] ForbiddenCharacters = ['<', '>', ':', '"', '|', '?', '*'];

    public ModRelativePath(string value)
    {
        if (!TryNormalize(value, out var normalized))
        {
            throw new ArgumentException(
                "Mod metadata paths must be safe relative paths without traversal, empty segments, or Windows-forbidden characters.",
                nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public bool IsValid => TryNormalize(Value, out _);

    public static bool TryCreate(string? value, out ModRelativePath relativePath)
    {
        if (!TryNormalize(value, out var normalized))
        {
            relativePath = default;
            return false;
        }

        relativePath = new ModRelativePath(normalized);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;

    private static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumLength
            || Path.IsPathFullyQualified(value))
        {
            return false;
        }

        var candidate = value.Replace('\\', '/');
        if (candidate.StartsWith("/", StringComparison.Ordinal)
            || candidate.EndsWith("/", StringComparison.Ordinal)
            || candidate.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in candidate.Split('/'))
        {
            if (segment is "." or ".."
                || string.IsNullOrWhiteSpace(segment)
                || segment.EndsWith(' ')
                || segment.EndsWith('.')
                || IsWindowsDeviceName(segment)
                || segment.Any(character => char.IsControl(character)
                    || ForbiddenCharacters.Contains(character)))
            {
                return false;
            }
        }

        normalized = candidate;
        return true;
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var baseName = segment.Split('.')[0];
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return baseName.Length == 4
            && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && baseName[3] is >= '1' and <= '9';
    }
}
