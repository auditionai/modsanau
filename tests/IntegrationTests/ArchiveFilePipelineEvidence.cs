namespace IntegrationTests;

internal static class ArchiveFilePipelineEvidence
{
    public static ArchiveFilePipelineEvidenceResult Verify(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> reextracted,
        IReadOnlyDictionary<string, string> expectedChanges)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(reextracted);
        ArgumentNullException.ThrowIfNull(expectedChanges);

        if (!TryNormalize(baseline, out var normalizedBaseline)
            || !TryNormalize(reextracted, out var normalizedReextracted)
            || !TryNormalize(expectedChanges, out var normalizedExpected))
        {
            return ArchiveFilePipelineEvidenceResult.Failure("ARCHIVE_PIPELINE_EVIDENCE_PATH_INVALID");
        }

        var missing = normalizedBaseline.Keys
            .Except(normalizedReextracted.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var added = normalizedReextracted.Keys
            .Except(normalizedBaseline.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0 || added.Length > 0)
        {
            return ArchiveFilePipelineEvidenceResult.Failure(
                "ARCHIVE_PIPELINE_EVIDENCE_INVENTORY_MISMATCH", missing, added);
        }

        var actualChanges = normalizedBaseline
            .Where(item => !HashEquals(item.Value, normalizedReextracted[item.Key]))
            .Select(item => item.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedPaths = normalizedExpected.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!actualChanges.SequenceEqual(expectedPaths, StringComparer.OrdinalIgnoreCase))
        {
            return ArchiveFilePipelineEvidenceResult.Failure(
                "ARCHIVE_PIPELINE_EVIDENCE_CHANGED_SET_MISMATCH",
                actualChangedPaths: actualChanges);
        }

        foreach (var expected in normalizedExpected)
        {
            if (!normalizedReextracted.TryGetValue(expected.Key, out var actual)
                || !HashEquals(expected.Value, actual))
            {
                return ArchiveFilePipelineEvidenceResult.Failure(
                    "ARCHIVE_PIPELINE_EVIDENCE_TARGET_HASH_MISMATCH",
                    actualChangedPaths: actualChanges);
            }
        }

        return ArchiveFilePipelineEvidenceResult.Success(
            normalizedBaseline.Count,
            actualChanges,
            normalizedBaseline.Count - actualChanges.Length);
    }

    private static bool TryNormalize(
        IReadOnlyDictionary<string, string> source,
        out Dictionary<string, string> normalized)
    {
        normalized = new(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source)
        {
            var path = item.Key?.Replace('\\', '/').Trim('/');
            if (string.IsNullOrWhiteSpace(path)
                || Path.IsPathFullyQualified(path)
                || path.Split('/').Any(segment => segment is "." or ".." || segment.Length == 0)
                || string.IsNullOrWhiteSpace(item.Value)
                || !normalized.TryAdd(path, item.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HashEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

internal sealed record ArchiveFilePipelineEvidenceResult(
    bool Succeeded,
    string DiagnosticCode,
    int FileCount,
    int NonTargetIdenticalCount,
    IReadOnlyList<string> ActualChangedPaths,
    IReadOnlyList<string> MissingPaths,
    IReadOnlyList<string> AddedPaths)
{
    public static ArchiveFilePipelineEvidenceResult Success(
        int fileCount,
        IReadOnlyList<string> actualChangedPaths,
        int nonTargetIdenticalCount) =>
        new(
            true,
            "ARCHIVE_PIPELINE_EVIDENCE_VALID",
            fileCount,
            nonTargetIdenticalCount,
            actualChangedPaths,
            [],
            []);

    public static ArchiveFilePipelineEvidenceResult Failure(
        string diagnosticCode,
        IReadOnlyList<string>? missingPaths = null,
        IReadOnlyList<string>? addedPaths = null,
        IReadOnlyList<string>? actualChangedPaths = null) =>
        new(false, diagnosticCode, 0, 0, actualChangedPaths ?? [], missingPaths ?? [], addedPaths ?? []);
}
