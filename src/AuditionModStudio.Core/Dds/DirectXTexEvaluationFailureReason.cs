namespace AuditionModStudio.Core.Dds;

public enum DirectXTexEvaluationFailureReason
{
    None,
    InvalidRequest,
    ToolMissing,
    ToolIntegrityMismatch,
    InputMissing,
    UnsupportedInput,
    InvalidInput,
    InputTooLarge,
    ProcessStartFailed,
    ToolFailed,
    OutputMissing,
    OutputInvalid,
    Cancelled,
    TimedOut
}
