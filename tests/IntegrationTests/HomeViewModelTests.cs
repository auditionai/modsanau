using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using AuditionModStudio.App.Home;
using AuditionModStudio.App.Shell;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Tasks;

namespace IntegrationTests;

public sealed class HomeViewModelTests
{
    private static readonly GameDefinition Audition = new(new GameId("audition"), "Audition");
    private static readonly GameDefinition OtherGame = new(new GameId("other_game"), "Other Game");
    private static readonly ModDefinition LoginMod = CreateMod();

    [Fact]
    public void Game_selection_filters_compatible_mods_and_resets_stale_mod_selection()
    {
        var viewModel = CreateViewModel([Audition, OtherGame], [LoginMod]);

        viewModel.SelectedGame = viewModel.Games[0];
        viewModel.SelectedMod = Assert.Single(viewModel.CompatibleMods);
        viewModel.ProjectName = "Login Refresh";

        Assert.True(viewModel.CanCreate);
        Assert.Equal(new ModId("login_screen"), viewModel.SelectedMod.ModId);

        viewModel.SelectedGame = viewModel.Games[1];

        Assert.Null(viewModel.SelectedMod);
        Assert.Empty(viewModel.CompatibleMods);
        Assert.False(viewModel.CanCreate);
        Assert.Contains("No compatible Mod Types", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_authoritative_mod_catalog_is_an_explicit_non_actionable_state()
    {
        var viewModel = CreateViewModel([Audition], []);

        viewModel.SelectedGame = Assert.Single(viewModel.Games);
        viewModel.ProjectName = "Project";

        Assert.False(viewModel.HasCompatibleMods);
        Assert.False(viewModel.CanCreate);
        Assert.Contains("No compatible Mod Types", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_uses_typed_project_service_through_background_manager()
    {
        var projectService = new RecordingProjectCreationService();
        var taskManager = new ImmediateBackgroundTaskManager();
        var viewModel = CreateViewModel(
            [Audition],
            [LoginMod],
            projectService,
            taskManager);
        viewModel.SelectedGame = Assert.Single(viewModel.Games);
        viewModel.SelectedMod = Assert.Single(viewModel.CompatibleMods);
        viewModel.ProjectName = "Dự án Login";

        await viewModel.CreateProjectAsync();

        Assert.Equal(BackgroundTaskKind.Extract, taskManager.EnqueuedKind);
        Assert.Equal(
            new ProjectCreationRequest(new GameId("audition"), new ModId("login_screen"), "Dự án Login"),
            projectService.Request);
        Assert.False(viewModel.IsCreating);
        Assert.Contains("could not be verified", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("bad\nname")]
    public void Invalid_project_name_never_enables_creation(string name)
    {
        var viewModel = CreateViewModel([Audition], [LoginMod]);
        viewModel.SelectedGame = Assert.Single(viewModel.Games);
        viewModel.SelectedMod = Assert.Single(viewModel.CompatibleMods);

        viewModel.ProjectName = name;

        Assert.False(viewModel.CanCreate);
        Assert.NotEmpty(viewModel.ProjectNameValidationMessage);
    }

    private static HomeViewModel CreateViewModel(
        ImmutableArray<GameDefinition> games,
        ImmutableArray<ModDefinition> mods,
        IProjectCreationService? projectCreationService = null,
        IBackgroundTaskManager? taskManager = null) => new(
            new TestGameCatalog(games),
            new TestModCatalog(mods),
            projectCreationService ?? new RecordingProjectCreationService(),
            taskManager ?? new ImmediateBackgroundTaskManager(),
            new TestProjectSession());

    private static ModDefinition CreateMod() => new(
        new ModId("login_screen"),
        new GameId("audition"),
        "Login Screen",
        new ModCategory("interface"),
        new ModRelativePath("Mods/Covers/login.png"),
        "Customize the Audition login screen.",
        new AuditionArchiveTemplate(
            "login-template",
            "015.ab",
            "Templates/015.ab",
            ArchiveEngineType.AcvTool5,
            "audition_vn",
            "015",
            "1",
            new string('A', 64),
            "audition-vn-current"),
        ModKeydatStrategy.ReuseOrGenerate,
        new ModRelativePath("Data/015.ab"),
        "Audition Vietnam current build.");

    private sealed class TestGameCatalog(ImmutableArray<GameDefinition> games) : IGameCatalog
    {
        public ImmutableArray<GameDefinition> GetGames() => games;

        public bool TryGetGame(GameId gameId, [NotNullWhen(true)] out GameDefinition? game)
        {
            game = games.FirstOrDefault(candidate => candidate.Id == gameId);
            return game is not null;
        }
    }

    private sealed class TestModCatalog(ImmutableArray<ModDefinition> mods) : IModCatalog
    {
        public ImmutableArray<ModDefinition> GetMods(GameId gameId) =>
            mods.Where(mod => mod.GameId == gameId).ToImmutableArray();

        public bool TryGetMod(GameId gameId, ModId modId, [NotNullWhen(true)] out ModDefinition? mod)
        {
            mod = mods.FirstOrDefault(candidate => candidate.GameId == gameId && candidate.Id == modId);
            return mod is not null;
        }
    }

    private sealed class RecordingProjectCreationService : IProjectCreationService
    {
        public ProjectCreationRequest? Request { get; private set; }

        public Task<ProjectCreationResult> CreateAsync(
            ProjectCreationRequest request,
            IProgress<ProjectCreationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            progress?.Report(new ProjectCreationProgress(ProjectCreationPhase.CheckingEntitlement, 1, 10));
            return Task.FromResult(ProjectCreationResult.Failure(
                ProjectCreationFailureReason.EntitlementUnavailable,
                "test.entitlement_unavailable"));
        }
    }

    private sealed class ImmediateBackgroundTaskManager : IBackgroundTaskManager
    {
        private BackgroundTaskSnapshot? _snapshot;

        public event EventHandler<BackgroundTaskNotification>? Notification
        {
            add { }
            remove { }
        }

        public BackgroundTaskKind EnqueuedKind { get; private set; }

        public async ValueTask<BackgroundTaskEnqueueResult> EnqueueAsync(
            BackgroundTaskRequest request,
            CancellationToken cancellationToken = default)
        {
            EnqueuedKind = request.Kind;
            var id = new BackgroundTaskId(Guid.NewGuid());
            var result = await request.Operation(new Progress<BackgroundTaskProgress>(), cancellationToken);
            _snapshot = new BackgroundTaskSnapshot(
                id,
                request.Kind,
                result.Succeeded ? BackgroundTaskState.Succeeded : BackgroundTaskState.Failed,
                null,
                result.DiagnosticCode,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            return BackgroundTaskEnqueueResult.Success(id);
        }

        public bool TryCancel(BackgroundTaskId taskId) => false;

        public bool TryGetSnapshot(BackgroundTaskId taskId, out BackgroundTaskSnapshot snapshot)
        {
            snapshot = _snapshot!;
            return _snapshot is not null && _snapshot.TaskId == taskId;
        }

        public ImmutableArray<BackgroundTaskSnapshot> GetSnapshots() =>
            _snapshot is null ? [] : [_snapshot];

        public Task<BackgroundTaskSnapshot?> WaitForCompletionAsync(
            BackgroundTaskId taskId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot is { } snapshot && snapshot.TaskId == taskId ? snapshot : null);
    }

    private sealed class TestProjectSession : IApplicationProjectSession
    {
        public AuditionProject? Project => null;
        public IProjectArchiveWorkspace? Workspace => null;

        public ValueTask ActivateAsync(AuditionProject project, IProjectArchiveWorkspace workspace) =>
            ValueTask.CompletedTask;

        public ValueTask<bool> TryUpdateProjectAsync(
            AuditionProject expectedProject,
            IProjectArchiveWorkspace expectedWorkspace,
            AuditionProject updatedProject) => ValueTask.FromResult(false);
    }
}
