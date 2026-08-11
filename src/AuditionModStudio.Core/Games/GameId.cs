namespace AuditionModStudio.Core.Games;

public readonly record struct GameId
{
    public const int MaximumLength = 64;

    public GameId(string value)
    {
        if (!IsValidValue(value))
        {
            throw new ArgumentException(
                "Game IDs must use 1-64 lowercase ASCII letters, digits, underscores, or hyphens and start with a letter or digit.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public bool IsValid => IsValidValue(Value);

    public static bool TryCreate(string? value, out GameId gameId)
    {
        if (!IsValidValue(value))
        {
            gameId = default;
            return false;
        }

        gameId = new GameId(value!);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;

    private static bool IsValidValue(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > MaximumLength
            || !IsLowerAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.All(character =>
            IsLowerAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private static bool IsLowerAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
