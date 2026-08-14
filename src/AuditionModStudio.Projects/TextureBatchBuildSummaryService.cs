using System.Collections.Immutable;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Projects;

public sealed class TextureBatchBuildSummaryService(IProjectBuildService buildService)
    : ITextureBatchBuildSummaryService
{
    public async Task<TextureBatchBuildResult> BuildAsync(
        TextureBatchBuildRequest request,
        IProgress<ProjectBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(request);
        if (prepared.Failure is not null)
        {
            return prepared.Failure;
        }

        var summary = prepared.Summary!;
        if (summary.ChangedCount == 0)
        {
            return TextureBatchBuildResult.Failure(
                TextureBatchBuildFailureReason.NoApprovedChanges,
                "TEXTURE_BATCH_BUILD_NO_APPROVED_CHANGES",
                summary);
        }

        var build = await buildService.BuildAsync(
            new(request.Project, request.Workspace),
            progress,
            cancellationToken).ConfigureAwait(false);
        if (build.Cancelled)
        {
            return TextureBatchBuildResult.CancelledResult(summary, build);
        }

        return build.Succeeded
            ? TextureBatchBuildResult.Success(summary, build)
            : TextureBatchBuildResult.Failure(
                TextureBatchBuildFailureReason.BuildFailed,
                "TEXTURE_BATCH_BUILD_FAILED",
                summary,
                build);
    }

    private static PreparedRequest Prepare(TextureBatchBuildRequest? request)
    {
        if (request?.Project is null || request.Workspace is null || request.Outcomes is null)
        {
            return PreparedRequest.Invalid(
                TextureBatchBuildFailureReason.InvalidRequest,
                "TEXTURE_BATCH_BUILD_REQUEST_INVALID");
        }

        var outcomes = request.Outcomes.ToArray();
        if (outcomes.Any(item => item is null
                                 || !item.RelativePath.IsValid
                                 || !Enum.IsDefined(item.Status)
                                 || !IsValidDiagnosticCode(item.DiagnosticCode)))
        {
            return PreparedRequest.Invalid(
                TextureBatchBuildFailureReason.InvalidRequest,
                "TEXTURE_BATCH_BUILD_OUTCOME_INVALID");
        }

        var duplicate = outcomes
            .GroupBy(item => item.RelativePath.Value, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() != 1);
        if (duplicate)
        {
            return PreparedRequest.Invalid(
                TextureBatchBuildFailureReason.InvalidRequest,
                "TEXTURE_BATCH_BUILD_OUTCOME_DUPLICATE");
        }

        var ordered = outcomes
            .OrderBy(item => item.RelativePath.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var summary = new TextureBatchBuildSummary(
            ordered,
            ordered.Count(item => item.Status == TextureBatchOutcomeStatus.Changed),
            ordered.Count(item => item.Status == TextureBatchOutcomeStatus.Failed),
            ordered.Count(item => item.Status == TextureBatchOutcomeStatus.Skipped));
        var reportedChanges = ordered
            .Where(item => item.Status == TextureBatchOutcomeStatus.Changed)
            .Select(item => item.RelativePath.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectChanges = request.Project.EditedTextures
            .Select(item => item.RelativePath.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!reportedChanges.SetEquals(projectChanges))
        {
            return new(summary, TextureBatchBuildResult.Failure(
                TextureBatchBuildFailureReason.OutcomeMismatch,
                "TEXTURE_BATCH_BUILD_CHANGED_SET_MISMATCH",
                summary));
        }

        return new(summary, null);
    }

    private static bool IsValidDiagnosticCode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character => character is >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_'
            or '-'
            or '.');

    private sealed record PreparedRequest(
        TextureBatchBuildSummary? Summary,
        TextureBatchBuildResult? Failure)
    {
        public static PreparedRequest Invalid(TextureBatchBuildFailureReason reason, string code) =>
            new(null, TextureBatchBuildResult.Failure(reason, code));
    }
}
