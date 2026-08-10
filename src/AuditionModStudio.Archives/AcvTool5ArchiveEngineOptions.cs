namespace AuditionModStudio.Archives;

public sealed record AcvTool5ArchiveEngineOptions(
    string TrustedToolSourceRoot,
    string ToolSourceRelativePath,
    int MaximumDiagnosticCharacters = 1_048_576);
