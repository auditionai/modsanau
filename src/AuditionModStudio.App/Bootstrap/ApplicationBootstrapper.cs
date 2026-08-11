using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Startup;
using AuditionModStudio.Core.Settings;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Infrastructure.Logging;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Infrastructure.Startup;
using AuditionModStudio.Infrastructure.Settings;
using AuditionModStudio.Infrastructure.Workspaces;
using AuditionModStudio.Imaging;
using AuditionModStudio.Mods;
using AuditionModStudio.Projects;
using AuditionModStudio.Dds;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuditionModStudio.App.Bootstrap;

internal sealed class ApplicationBootstrapper : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly ApplicationLogSession _logSession;
    private int _disposeState;
    private int _startState;

    public ApplicationBootstrapper()
    {
        var paths = new AppPaths();
        var pathSecurity = new PathSecurity();
        pathSecurity.EnsureNoReparsePoints(paths.RootDirectory, paths.RootDirectory);
        paths.EnsureDirectoriesExist();
        foreach (var directory in paths.ManagedDirectories)
        {
            pathSecurity.EnsureNoReparsePoints(paths.RootDirectory, directory);
        }

        _logSession = new ApplicationLogSession(paths);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = typeof(App).Assembly.GetName().Name,
            Args = [],
        });

        _logSession.ConfigureServices(builder.Services);
        builder.Services.AddSingleton<IAppPaths>(paths);
        builder.Services.AddSingleton<IPathSecurity>(pathSecurity);
        builder.Services.AddSingleton<SecureWorkspaceService>();
        builder.Services.AddSingleton<ISecureWorkspaceService>(services =>
            services.GetRequiredService<SecureWorkspaceService>());
        builder.Services.AddSingleton<ISecureWorkspaceRecoveryService>(services =>
            services.GetRequiredService<SecureWorkspaceService>());
        builder.Services.AddSingleton<ISecureWorkspaceRetentionService>(services =>
            services.GetRequiredService<SecureWorkspaceService>());
        builder.Services.AddSingleton<ISecureWorkspaceRemovalService>(services =>
            services.GetRequiredService<SecureWorkspaceService>());
        builder.Services.AddSingleton<ISettingsValidator, SettingsValidator>();
        builder.Services.AddSingleton<IAtomicSettingsWriter, AtomicSettingsWriter>();
        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddHostedService<SettingsInitializationService>();
        builder.Services.AddSingleton(TrustedArchiveToolManifest.Production);
        builder.Services.AddSingleton<ArchiveToolIntegrityPolicy>();
        builder.Services.AddSingleton<IArchiveToolExecutionPolicy>(services =>
            services.GetRequiredService<ArchiveToolIntegrityPolicy>());
        builder.Services.AddSingleton<IKeydatService, KeydatService>();
        builder.Services.AddSingleton<IArchiveToolProvisioningService, ArchiveToolProvisioningService>();
        builder.Services.AddSingleton<IArchiveToolRunner, AcvTool5Runner>();
        builder.Services.AddSingleton<IGameRegionProfileResolver, GameRegionProfileCatalog>();
        var templateVersions = TemplateVersionCatalog.Create([]);
        if (!templateVersions.Succeeded)
        {
            throw new InvalidOperationException("Built-in template version catalog validation failed.");
        }
        builder.Services.AddSingleton(templateVersions.Catalog!);
        builder.Services.AddSingleton<IAuditionArchiveService, AuditionArchiveService>();
        builder.Services.AddSingleton<IProjectArchiveWorkspaceManifestStore, ProjectArchiveWorkspaceManifestStore>();
        builder.Services.AddSingleton<IProjectArchiveWorkspaceService, ProjectArchiveWorkspaceService>();
        builder.Services.AddSingleton<IProjectArchiveWorkspaceRecoveryService, ProjectArchiveWorkspaceRecoveryService>();
        builder.Services.AddSingleton<IAuditionProjectStore, AuditionProjectStore>();
        builder.Services.AddSingleton<IProjectMetadataCache, ProjectMetadataCache>();
        builder.Services.AddSingleton<ITemplateEntitlementService, UnavailableTemplateEntitlementService>();
        builder.Services.AddSingleton<IProjectTemplateAcquisitionService, UnavailableProjectTemplateAcquisitionService>();
        builder.Services.AddSingleton<IArchiveAssetScanner, ArchiveAssetScanner>();
        builder.Services.AddSingleton<IDdsMetadataReader, DdsMetadataReader>();
        builder.Services.AddSingleton(DirectXTexEvaluationToolCatalog.May2026X64);
        builder.Services.AddSingleton<IDirectXTexEvaluationHarness, DirectXTexEvaluationHarness>();
        builder.Services.AddSingleton(DdsPreviewServiceOptions.CreateProduction(AppContext.BaseDirectory));
        builder.Services.AddSingleton<IDdsPreviewService, DdsPreviewService>();
        builder.Services.AddSingleton(DdsEncoderOptions.CreateProduction(AppContext.BaseDirectory));
        builder.Services.AddSingleton<IDdsEncoder, DdsEncoder>();
        builder.Services.AddSingleton<IDdsValidationService, DdsValidationService>();
        builder.Services.AddSingleton<IDdsMatchOriginalService, DdsMatchOriginalService>();
        builder.Services.AddSingleton(ImageImportResourcePolicy.Default);
        builder.Services.AddSingleton<IImageImportService, ImageImportService>();
        builder.Services.AddSingleton<IImageResizeService, ImageResizeService>();
        builder.Services.AddSingleton<IImageTransformService, ImageTransformService>();
        builder.Services.AddSingleton<IImageAdjustmentService, ImageAdjustmentService>();
        builder.Services.AddSingleton<IAlphaChannelService, AlphaChannelService>();
        builder.Services.AddSingleton<IEditHistoryService, EditHistoryService>();
        var gameCatalog = GameCatalog.CreateBuiltIn();
        builder.Services.AddSingleton(gameCatalog);
        builder.Services.AddSingleton<IModCatalog>(services =>
        {
            var result = ModCatalog.Create(
                [],
                gameCatalog,
                services.GetRequiredService<IGameRegionProfileResolver>());
            if (!result.Succeeded)
            {
                var diagnostics = string.Join(",", result.Issues.Select(issue => issue.DiagnosticCode));
                throw new InvalidOperationException($"Built-in mod catalog validation failed: {diagnostics}");
            }

            return result.Catalog!;
        });
        builder.Services.AddSingleton<ITextureManifestCatalog>(services =>
        {
            var result = TextureManifestCatalog.Create(
                [],
                services.GetRequiredService<IModCatalog>());
            if (!result.Succeeded)
            {
                var diagnostics = string.Join(",", result.Issues.Select(issue => issue.DiagnosticCode));
                throw new InvalidOperationException($"Built-in texture manifest catalog validation failed: {diagnostics}");
            }

            return result.Catalog!;
        });
        builder.Services.AddSingleton<ISmartModScanService, SmartModScanService>();
        builder.Services.AddSingleton<IProjectCreationService, ProjectCreationService>();
        builder.Services.AddSingleton<IProjectLoadService, ProjectLoadService>();
        builder.Services.AddSingleton<ITextureStateMachine, TextureStateMachine>();
        builder.Services.AddSingleton<IProjectTextureRestoreService, ProjectTextureRestoreService>();
        builder.Services.AddSingleton<IProjectResetService, ProjectResetService>();
        builder.Services.AddSingleton<IStartupValidator, StartupValidator>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
    }

    public IServiceProvider Services => _host.Services;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);

        if (Interlocked.Exchange(ref _startState, 1) != 0)
        {
            throw new InvalidOperationException("Application bootstrap has already started.");
        }

        var logger = Services.GetRequiredService<ILogger<ApplicationBootstrapper>>();

        try
        {
            logger.LogInformation("Application bootstrap is starting");
            var validator = Services.GetRequiredService<IStartupValidator>();
            await validator.ValidateAsync(cancellationToken).ConfigureAwait(false);
            await _host.StartAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Application bootstrap completed");
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Application bootstrap failed");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        var logger = Services.GetRequiredService<ILogger<ApplicationBootstrapper>>();

        try
        {
            logger.LogInformation("Application shutdown is starting");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _host.StopAsync(timeout.Token).ConfigureAwait(false);
            logger.LogInformation("Application host stopped");
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Application shutdown failed");
            throw;
        }
        finally
        {
            _host.Dispose();
            _logSession.Dispose();
        }
    }

    public void FlushLogsForProcessTermination()
    {
        _logSession.Dispose();
    }
}
