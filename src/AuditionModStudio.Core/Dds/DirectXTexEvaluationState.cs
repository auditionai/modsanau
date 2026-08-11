namespace AuditionModStudio.Core.Dds;

public enum DirectXTexEvaluationState
{
    Starting,
    ValidatingInput,
    ProvisioningTool,
    Running,
    VerifyingOutput,
    Completed,
    Failed,
    Cancelled,
    TimedOut
}
