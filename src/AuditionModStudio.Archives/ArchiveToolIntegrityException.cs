namespace AuditionModStudio.Archives;

public sealed class ArchiveToolIntegrityException : Exception
{
    public ArchiveToolIntegrityException(ArchiveToolIntegrityResult result)
        : base($"Archive tool execution was rejected: {result.FailureReason}.")
    {
        Result = result;
    }

    public ArchiveToolIntegrityResult Result { get; }
}
