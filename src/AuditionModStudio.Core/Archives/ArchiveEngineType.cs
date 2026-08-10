namespace AuditionModStudio.Core.Archives;

public sealed record ArchiveEngineType
{
    public static ArchiveEngineType AcvTool5 { get; } = new("acv_tool_5");

    public ArchiveEngineType(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 64 || id.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new ArgumentException("Archive engine IDs must use 1-64 ASCII letters, digits, underscores, or hyphens.", nameof(id));
        }

        Id = id;
    }

    public string Id { get; }

    public override string ToString() => Id;
}
