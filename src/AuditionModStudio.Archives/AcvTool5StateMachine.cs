namespace AuditionModStudio.Archives;

internal sealed class AcvTool5StateMachine(AcvTool5Operation operation)
{
    private readonly AcvTool5RunnerState _operationState = operation == AcvTool5Operation.Extract
        ? AcvTool5RunnerState.Extracting
        : AcvTool5RunnerState.Packing;

    public AcvTool5RunnerState Current { get; private set; } = AcvTool5RunnerState.Starting;

    public void TransitionTo(AcvTool5RunnerState next)
    {
        if (!IsAllowed(Current, next))
        {
            throw new InvalidOperationException($"Invalid ACV Tool 5 state transition: {Current} -> {next}.");
        }

        Current = next;
    }

    private bool IsAllowed(AcvTool5RunnerState current, AcvTool5RunnerState next) => current switch
    {
        AcvTool5RunnerState.Starting => next == _operationState || IsEarlyTerminal(next),
        AcvTool5RunnerState.Extracting or AcvTool5RunnerState.Packing =>
            next is AcvTool5RunnerState.WaitingForCountrySelection
                or AcvTool5RunnerState.VerifyingArtifacts
                or AcvTool5RunnerState.Failed
                or AcvTool5RunnerState.Cancelled
                or AcvTool5RunnerState.TimedOut,
        AcvTool5RunnerState.WaitingForCountrySelection =>
            next == _operationState
                || next is AcvTool5RunnerState.Failed
                    or AcvTool5RunnerState.Cancelled
                    or AcvTool5RunnerState.TimedOut,
        AcvTool5RunnerState.VerifyingArtifacts =>
            next is AcvTool5RunnerState.Completed or AcvTool5RunnerState.Failed,
        _ => false,
    };

    private static bool IsEarlyTerminal(AcvTool5RunnerState state) => state is
        AcvTool5RunnerState.Failed
        or AcvTool5RunnerState.Cancelled
        or AcvTool5RunnerState.TimedOut;
}
