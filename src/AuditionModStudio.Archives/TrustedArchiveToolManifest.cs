namespace AuditionModStudio.Archives;

public sealed class TrustedArchiveToolManifest
{
    private readonly IReadOnlyDictionary<string, ArchiveToolDescriptor> _tools;

    public TrustedArchiveToolManifest(IEnumerable<ArchiveToolDescriptor> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var descriptors = tools.ToArray();
        if (descriptors.Any(static descriptor => descriptor is null)
            || descriptors.GroupBy(static descriptor => descriptor.ToolId, StringComparer.Ordinal)
                .Any(static group => group.Count() != 1))
        {
            throw new ArgumentException("The trusted tool manifest contains null or duplicate entries.", nameof(tools));
        }

        _tools = descriptors.ToDictionary(
            static descriptor => descriptor.ToolId,
            StringComparer.Ordinal);
    }

    public static TrustedArchiveToolManifest Production { get; } = new(
    [
        new ArchiveToolDescriptor(
            ArchiveToolIds.AcvTool5,
            "acv.exe",
            "6A52C808D7E5A59EB41E43D86A32E78067F424C8531E34981887F093E81547D3",
            ExpectedVersion: null,
            IsApproved: true),
    ]);

    public bool TryGetDescriptor(string toolId, out ArchiveToolDescriptor descriptor) =>
        _tools.TryGetValue(toolId, out descriptor!);
}
