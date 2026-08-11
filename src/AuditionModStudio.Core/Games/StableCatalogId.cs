namespace AuditionModStudio.Core.Games;

internal static class StableCatalogId
{
    public const int MaximumLength = 64;

    public static bool IsValid(string? value)
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
