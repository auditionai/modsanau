using System.Text.Json;

namespace Security.Tests;

public sealed class SecurityTestMatrixTests
{
    [Fact]
    public void Plan89_matrix_has_exact_control_set_and_resolvable_evidence()
    {
        var root = FindRepositoryRoot();
        var matrixPath = Path.Combine(root, "docs", "security-test-matrix.json");
        using var document = JsonDocument.Parse(File.ReadAllText(matrixPath));
        var matrix = document.RootElement;

        Assert.Equal(1, matrix.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(89, matrix.GetProperty("plan").GetInt32());
        var controls = matrix.GetProperty("controls").EnumerateArray().ToArray();
        Assert.Equal(Enumerable.Range(1, 12).Select(value => $"STM-{value:D2}"),
            controls.Select(control => control.GetProperty("id").GetString()));
        Assert.All(controls, control =>
        {
            Assert.False(string.IsNullOrWhiteSpace(control.GetProperty("requirement").GetString()));
            Assert.NotEmpty(control.GetProperty("evidence").EnumerateArray());
        });

        var evidence = string.Join('\n', controls.SelectMany(control =>
            control.GetProperty("evidence").EnumerateArray().Select(item => item.GetString())));
        foreach (var marker in new[]
        {
            "Modified_byte_is_rejected_with_hash_mismatch",
            "Expected_source_hash_mismatch_returns_structured_failure",
            "PathSecurityTests",
            "Client_cannot_mutate_credit_or_send_client_authority_fields",
            "Expired_overlong_or_cross_subject_token_is_rejected_fail_closed",
            "Duplicate_ai_enqueue_reserves_credit_exactly_once_and_replays_one_job",
            "Parallel_reservations_cannot_overspend_one_wallet",
            "Wrong_user_cannot_get_list_or_cancel_another_users_ai_job",
            "AppUpdateVerificationTests",
            "Invoke-SecretScan.ps1",
            "Package_bytes_require_exact_length_hash_and_manifest_signature",
            "Real_acv_malformed_archive_fails_structurally_without_escape_or_process_exception",
        }) Assert.Contains(marker, evidence, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(root, "scripts", "Invoke-SecurityTestMatrix.ps1")));
        Assert.True(File.Exists(Path.Combine(root, "docs", "SECURITY_TEST_MATRIX.md")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
