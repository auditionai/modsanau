using System.Security.Cryptography;
using System.Text;

namespace IntegrationTests;

internal enum CrashRecoveryClassification
{
    Committed,
    RecoverableWorkspace,
    IncompleteTransaction,
    StaleResidue,
    CorruptArtifact,
    UnknownUntrusted,
}

internal enum CrashSimulationKind
{
    ProcessExit,
    Cancellation,
    IoFailure,
    Corruption,
    DeterministicCheckpoint,
    ConcurrentReplay,
}

internal sealed record CrashRecoveryMatrixCase(
    string CaseId,
    string Operation,
    string CrashPoint,
    CrashSimulationKind Simulation,
    CrashRecoveryClassification FailureState,
    string RecoveryRule,
    string EvidenceTest);

internal static class CrashRecoveryEvidence
{
    public static IReadOnlyList<CrashRecoveryMatrixCase> Cases { get; } =
        Array.AsReadOnly<CrashRecoveryMatrixCase>(
    [
        new("archive-apply-before-promote", "Áp dụng DDS", "candidate đã validate, lưu project lỗi trước commit", CrashSimulationKind.IoFailure,
            CrashRecoveryClassification.IncompleteTransaction, "khôi phục đúng byte DDS đang làm việc, xóa history mới và cho phép retry sạch",
            "Projects.Tests.TextureApplyServiceTests.Project_save_failure_rolls_back_target_and_new_history_assets"),
        new("archive-build-pack-before-promote", "Build archive", "pack hoặc lần lưu cuối lỗi quanh bước promote output", CrashSimulationKind.IoFailure,
            CrashRecoveryClassification.IncompleteTransaction, "không công bố output dở dang và khôi phục đúng byte output cũ nếu đã promote",
            "Projects.Tests.ProjectBuildServiceTests.Exception_after_promotion_still_restores_previous_output_bytes"),
        new("archive-export-after-durable-copy", "Export archive standalone", "copy bền vững vào file tạm xong trước promote đích", CrashSimulationKind.IoFailure,
            CrashRecoveryClassification.IncompleteTransaction, "giữ nguyên byte đích, xóa residue giao dịch và copy lại khi retry",
            "Projects.Tests.ArchiveExportServiceTests.One_shot_failure_after_durable_copy_preserves_destination_and_retry_succeeds"),
        new("archive-extract-staging", "Extract archive", "tool thoát sau khi ghi một DDS vào staging", CrashSimulationKind.ProcessExit,
            CrashRecoveryClassification.IncompleteTransaction, "cây staging không phải trạng thái project đã commit và retry sạch phải thành công",
            "Archives.Tests.AcvTool5RunnerTests.Extract_process_failure_after_staging_write_is_incomplete_and_clean_retry_succeeds"),
        new("archive-scan-preparation", "Quét Smart Mod", "hủy khi các tác vụ metadata đang chạy", CrashSimulationKind.Cancellation,
            CrashRecoveryClassification.IncompleteTransaction, "không công bố kết quả quét một phần và retry tính lại inventory xác định",
            "Projects.Tests.SmartModScanServiceTests.Cancellation_is_structured_and_does_not_publish_partial_result"),
        new("cache-encrypted-mid-chunk", "Cache template mã hóa", "sau khi chunk mã hóa đầu tiên vào entry tạm", CrashSimulationKind.Cancellation,
            CrashRecoveryClassification.IncompleteTransaction, "giữ đúng ciphertext đã commit, xóa temp và retry từ nguồn",
            "Security.Tests.EncryptedPremiumTemplateCacheTests.Crash_after_first_encrypted_chunk_preserves_old_entry_and_retry_recovers"),
        new("payment-duplicate-delivery", "Fulfillment thanh toán", "phân phối đồng thời cùng một provider event đã xác minh", CrashSimulationKind.ConcurrentReplay,
            CrashRecoveryClassification.Committed, "server ledger chỉ áp dụng một lần và mọi bản trùng trả idempotent replay",
            "Gateway.Tests.CreditConcurrencyIntegrationTests.Concurrent_verified_payment_delivery_applies_once_and_replays_without_double_credit"),
        new("template-after-immutable-commit", "Publish template admin", "fault ngay sau khi move thư mục immutable", CrashSimulationKind.DeterministicCheckpoint,
            CrashRecoveryClassification.Committed, "giữ phiên bản immutable đầy đủ và từ chối retry trùng",
            "Archives.Tests.AtomicFileTemplateAdminPublisherTests.Crash_after_immutable_commit_keeps_complete_version_and_retry_cannot_duplicate_it"),
        new("template-before-immutable-commit", "Publish template admin", "package mã hóa hoặc metadata đã ghi trước move immutable", CrashSimulationKind.DeterministicCheckpoint,
            CrashRecoveryClassification.IncompleteTransaction, "xóa staging, giữ phiên bản trước và cho phép retry sạch",
            "Archives.Tests.AtomicFileTemplateAdminPublisherTests.Crash_before_immutable_commit_preserves_published_version_and_clean_retry_succeeds"),
        new("updater-before-installer-handoff", "Cập nhật ứng dụng", "candidate đã xác minh bị installer handoff từ chối", CrashSimulationKind.IoFailure,
            CrashRecoveryClassification.IncompleteTransaction, "xóa operation root và retry phải download, hash, verify lại",
            "Security.Tests.AppUpdateVerificationTests.Failed_verified_handoff_is_cleaned_and_retry_redownloads_and_reverifies"),
        new("updater-partial-download", "Cập nhật ứng dụng", "download bị hủy trước promote candidate", CrashSimulationKind.Cancellation,
            CrashRecoveryClassification.IncompleteTransaction, "xóa partial và operation root, tuyệt đối không gọi installer",
            "Security.Tests.AppUpdateVerificationTests.Cancellation_cleans_staging_and_never_installs"),
        new("workspace-abandoned-retained", "Secure workspace", "process biến mất sau retention marker và nhả lock", CrashSimulationKind.ProcessExit,
            CrashRecoveryClassification.RecoverableWorkspace, "phát hiện không mutation và chỉ recover đúng candidate sau thao tác tường minh",
            "IntegrationTests.SecureWorkspaceTests.Startup_detects_stale_retained_workspace_without_automatic_mutation"),
        new("workspace-unknown-reparse", "Inventory recovery", "residue lạ hoặc dựa trên reparse xuất hiện dưới managed root", CrashSimulationKind.Corruption,
            CrashRecoveryClassification.UnknownUntrusted, "không tự trust, tự promote hoặc đi theo residue",
            "IntegrationTests.Plan99CrashRecoveryMatrixTests.Residue_inventory_is_deterministic_and_unknown_state_never_becomes_trusted"),
    ]);

    public static string RenderMarkdownTable()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Case ID | Thao tác | Điểm crash | Mô phỏng | Trạng thái sau lỗi | Quy tắc recovery / retry | Bằng chứng thực thi |");
        builder.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var item in Cases.OrderBy(item => item.CaseId, StringComparer.Ordinal))
        {
            builder.Append("| `").Append(item.CaseId).Append("` | ")
                .Append(item.Operation).Append(" | ")
                .Append(item.CrashPoint).Append(" | ")
                .Append(Format(item.Simulation)).Append(" | ")
                .Append(Format(item.FailureState)).Append(" | ")
                .Append(item.RecoveryRule).Append(" | `")
                .Append(item.EvidenceTest).AppendLine("` |");
        }

        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Format(CrashSimulationKind value) => value switch
    {
        CrashSimulationKind.ProcessExit => "THOÁT TIẾN TRÌNH",
        CrashSimulationKind.Cancellation => "HỦY",
        CrashSimulationKind.IoFailure => "LỖI I/O",
        CrashSimulationKind.Corruption => "HỎNG DỮ LIỆU",
        CrashSimulationKind.DeterministicCheckpoint => "CHECKPOINT XÁC ĐỊNH",
        CrashSimulationKind.ConcurrentReplay => "REPLAY ĐỒNG THỜI",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string Format(CrashRecoveryClassification value) => value switch
    {
        CrashRecoveryClassification.Committed => "ĐÃ COMMIT",
        CrashRecoveryClassification.RecoverableWorkspace => "WORKSPACE CÓ THỂ RECOVER",
        CrashRecoveryClassification.IncompleteTransaction => "GIAO DỊCH DỞ DANG",
        CrashRecoveryClassification.StaleResidue => "RESIDUE CŨ",
        CrashRecoveryClassification.CorruptArtifact => "ARTIFACT HỎNG",
        CrashRecoveryClassification.UnknownUntrusted => "LẠ / KHÔNG TIN CẬY",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}

internal sealed class DeterministicCrashController(params string[] checkpoints)
{
    private readonly HashSet<string> _armed = new(checkpoints, StringComparer.Ordinal);

    public void Reach(string checkpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint);
        if (_armed.Remove(checkpoint))
        {
            throw new SimulatedCrashException(checkpoint);
        }
    }
}

internal sealed class SimulatedCrashException(string checkpoint)
    : IOException($"SIMULATED_CRASH:{checkpoint}");

internal sealed record CrashResidueEntry(string Name, CrashRecoveryClassification Classification);

internal static class CrashResidueInventory
{
    public static IReadOnlyList<CrashResidueEntry> Inspect(
        string root,
        IReadOnlyDictionary<string, string> expectedCommittedHashes)
    {
        var entries = new List<CrashResidueEntry>();
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories()
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            entries.Add(new(directory.Name, Classify(directory, expectedCommittedHashes)));
        }

        return entries;
    }

    private static CrashRecoveryClassification Classify(
        DirectoryInfo directory,
        IReadOnlyDictionary<string, string> expectedCommittedHashes)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return CrashRecoveryClassification.UnknownUntrusted;
        }

        if (File.Exists(Path.Combine(directory.FullName, ".corrupt")))
        {
            return CrashRecoveryClassification.CorruptArtifact;
        }

        if (directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(item =>
                item.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || item.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
                || item.Name.StartsWith(".stage-", StringComparison.Ordinal)))
        {
            return CrashRecoveryClassification.IncompleteTransaction;
        }

        if (File.Exists(Path.Combine(directory.FullName, ".workspace.json"))
            && File.Exists(Path.Combine(directory.FullName, ".retained")))
        {
            return CrashRecoveryClassification.RecoverableWorkspace;
        }

        var payload = Path.Combine(directory.FullName, "payload.bin");
        if (File.Exists(Path.Combine(directory.FullName, "commit.json"))
            && File.Exists(payload)
            && expectedCommittedHashes.TryGetValue(directory.Name, out var expectedHash))
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(payload)));
            return string.Equals(actualHash, expectedHash, StringComparison.Ordinal)
                ? CrashRecoveryClassification.Committed
                : CrashRecoveryClassification.CorruptArtifact;
        }

        if (File.Exists(Path.Combine(directory.FullName, ".stale")))
        {
            return CrashRecoveryClassification.StaleResidue;
        }

        return CrashRecoveryClassification.UnknownUntrusted;
    }
}
