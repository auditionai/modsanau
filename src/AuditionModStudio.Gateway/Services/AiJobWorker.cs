namespace AuditionModStudio.Gateway.Services;

public sealed class AiJobWorker(IAiJobExecutionService execution, ILogger<AiJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await execution.ExecuteNextAsync(stoppingToken).ConfigureAwait(false);
            if (result.DiagnosticCode == "AI_JOB_NONE_AVAILABLE")
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                continue;
            }
            if (!result.Succeeded)
                logger.LogWarning("AI worker completed with {DiagnosticCode} and {FinalState}",
                    result.DiagnosticCode, result.FinalState);
        }
    }
}
