using AuditionModStudio.Core.Workspaces;

namespace AuditionModStudio.Core.Dds;

public sealed record DirectXTexEvaluationRequest(
    DirectXTexEvaluationOperation Operation,
    ISecureWorkspace Workspace,
    string ToolSourcePath,
    string InputRelativePath,
    string OutputSubdirectory,
    int? TargetWidth,
    int? TargetHeight,
    int MipLevelCount,
    bool ForceLegacyHeader,
    TimeSpan Timeout,
    long MaximumInputBytes = 536_870_912,
    long MaximumPixelCount = 100_000_000,
    int MaximumDiagnosticCharacters = 262_144);
