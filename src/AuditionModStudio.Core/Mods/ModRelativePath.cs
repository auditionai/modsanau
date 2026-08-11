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
                || segment.Any(character => char.IsControl(character)
                    || ForbiddenCharacters.Contains(character)))
            {
                return false;
            }
        }

        normalized = candidate;
        return true;
    }
}
