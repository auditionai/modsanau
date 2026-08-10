namespace AuditionModStudio.Core.Workspaces;

public interface ISecureWorkspace : IAsyncDisposable
{
    string Id { get; }

    SecureWorkspacePaths Paths { get; }

    string ResolveRelativePath(string relativePath);
}
