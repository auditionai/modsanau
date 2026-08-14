namespace AuditionModStudio.Archives;

public sealed record ArchiveToolDescriptor(
    string ToolId,
    string ExpectedFileName,
    string ExpectedSha256,
    string? ExpectedVersion,
    bool IsApproved);
