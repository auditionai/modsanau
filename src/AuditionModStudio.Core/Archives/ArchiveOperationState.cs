namespace AuditionModStudio.Core.Archives;

public enum ArchiveOperationState
{
    Preparing,
    ProvisioningTool,
    PreparingKeydat,
    Extracting,
    Packing,
    Verifying,
    Completed,
    Failed,
    Cancelled,
    TimedOut,
}
