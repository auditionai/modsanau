using AuditionModStudio.Core.Workspaces;

namespace AuditionModStudio.Core.Dds;

public sealed record DdsPreviewRequest(
    ISecureWorkspace Workspace,
    string SourceRelativePath);
