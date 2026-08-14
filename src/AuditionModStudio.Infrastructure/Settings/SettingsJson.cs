using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using AuditionModStudio.Core.Settings;

namespace AuditionModStudio.Infrastructure.Settings;

internal static class SettingsJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }

    public static byte[] Serialize(ApplicationSettings settings)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
        var withNewLine = new byte[serialized.Length + 1];
        serialized.CopyTo(withNewLine, 0);
        withNewLine[^1] = (byte)'\n';
        return withNewLine;
    }
}
