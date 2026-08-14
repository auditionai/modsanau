namespace AuditionModStudio.Archives;

public enum AcvTool5RunnerState
{
    Starting,
    WaitingForCountrySelection,
    Extracting,
    Packing,
    VerifyingArtifacts,
    Completed,
    Failed,
    Cancelled,
    TimedOut,
}
