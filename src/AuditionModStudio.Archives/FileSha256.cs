using System.Security.Cryptography;

namespace AuditionModStudio.Archives;

internal static class FileSha256
{
    public static async Task<string> ComputeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    public static bool EqualsHex(string expected, string actual)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(actual));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsValidHex(string? value) => value is not null
        && value.Length == 64
        && value.All(static character => char.IsAsciiHexDigit(character));
}
