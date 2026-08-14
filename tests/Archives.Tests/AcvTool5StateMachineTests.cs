using AuditionModStudio.Archives;

namespace Archives.Tests;

public sealed class AcvTool5StateMachineTests
{
    [Fact]
    public void Extract_happy_path_is_deterministic()
    {
        var stateMachine = new AcvTool5StateMachine(AcvTool5Operation.Extract);

        stateMachine.TransitionTo(AcvTool5RunnerState.Extracting);
        stateMachine.TransitionTo(AcvTool5RunnerState.WaitingForCountrySelection);
        stateMachine.TransitionTo(AcvTool5RunnerState.Extracting);
        stateMachine.TransitionTo(AcvTool5RunnerState.VerifyingArtifacts);
        stateMachine.TransitionTo(AcvTool5RunnerState.Completed);

        Assert.Equal(AcvTool5RunnerState.Completed, stateMachine.Current);
    }

    [Fact]
    public void Invalid_transition_is_rejected()
    {
        var stateMachine = new AcvTool5StateMachine(AcvTool5Operation.Pack);

        var exception = Assert.Throws<InvalidOperationException>(
            () => stateMachine.TransitionTo(AcvTool5RunnerState.Completed));

        Assert.Contains("Starting -> Completed", exception.Message, StringComparison.Ordinal);
    }
}
