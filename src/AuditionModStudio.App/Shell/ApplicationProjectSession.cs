using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.App.Shell;

public interface IApplicationProjectSession
{
    AuditionProject? Project { get; }

    IProjectArchiveWorkspace? Workspace { get; }

    ValueTask ActivateAsync(
        AuditionProject project,
        IProjectArchiveWorkspace workspace);

    ValueTask<bool> TryUpdateProjectAsync(
        AuditionProject expectedProject,
        IProjectArchiveWorkspace expectedWorkspace,
        AuditionProject updatedProject);
}

public sealed class ApplicationProjectSession : IApplicationProjectSession, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IProjectArchiveWorkspace? _workspace;
    private int _disposed;

    public AuditionProject? Project { get; private set; }

    public IProjectArchiveWorkspace? Workspace => _workspace;

    public async ValueTask ActivateAsync(
        AuditionProject project,
        IProjectArchiveWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(workspace);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var previousWorkspace = _workspace;
            Project = project;
            _workspace = workspace;

            if (previousWorkspace is not null
                && !ReferenceEquals(previousWorkspace, workspace))
            {
                await previousWorkspace.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> TryUpdateProjectAsync(
        AuditionProject expectedProject,
        IProjectArchiveWorkspace expectedWorkspace,
        AuditionProject updatedProject)
    {
        ArgumentNullException.ThrowIfNull(expectedProject);
        ArgumentNullException.ThrowIfNull(expectedWorkspace);
        ArgumentNullException.ThrowIfNull(updatedProject);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!ReferenceEquals(Project, expectedProject)
                || !ReferenceEquals(_workspace, expectedWorkspace)
                || updatedProject.ProjectId != expectedProject.ProjectId)
            {
                return false;
            }

            Project = updatedProject;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_workspace is not null)
            {
                await _workspace.DisposeAsync().ConfigureAwait(false);
                _workspace = null;
            }

            Project = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
