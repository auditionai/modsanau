using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Workspaces;

namespace AuditionModStudio.Archives;

public sealed record AcvTool5RunRequest(
    AcvTool5Operation Operation,
    ISecureWorkspace Workspace,
    string ExecutablePath,
    string ArchiveRelativePath,
    string ExtractDirectoryRelativePath,
    GameRegionProfile RegionProfile,
    TimeSpan Timeout,
    int MaximumDiagnosticCharacters = 1_048_576);
