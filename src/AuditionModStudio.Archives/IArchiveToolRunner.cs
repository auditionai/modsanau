namespace AuditionModStudio.Archives;

public interface IArchiveToolRunner
{
    Task<AcvTool5RunResult> RunAsync(
        AcvTool5RunRequest request,
        IProgress<AcvTool5Progress>? progress = null,
        CancellationToken cancellationToken = default);
}
