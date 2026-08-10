namespace AuditionModStudio.Archives;

public sealed record AcvTool5Progress(
    AcvTool5Operation Operation,
    AcvTool5RunnerState State,
    string? CurrentItemPath,
    int ProcessedItemCount);
