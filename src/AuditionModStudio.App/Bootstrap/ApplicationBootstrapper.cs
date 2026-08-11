using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Dds;
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
        builder.Services.AddSingleton<ISecureWorkspaceService, SecureWorkspaceService>();
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
        builder.Services.AddSingleton<IAuditionArchiveService, AuditionArchiveService>();
        builder.Services.AddSingleton<IProjectArchiveWorkspaceManifestStore, ProjectArchiveWorkspaceManifestStore>();
        builder.Services.AddSingleton<IProjectArchiveWorkspaceService, ProjectArchiveWorkspaceService>();
        builder.Services.AddSingleton<IArchiveAssetScanner, ArchiveAssetScanner>();
        builder.Services.AddSingleton<IDdsMetadataReader, DdsMetadataReader>();
        builder.Services.AddSingleton(DirectXTexEvaluationToolCatalog.May2026X64);
        builder.Services.AddSingleton<IDirectXTexEvaluationHarness, DirectXTexEvaluationHarness>();
        builder.Services.AddSingleton(DdsPreviewServiceOptions.CreateProduction(AppContext.BaseDirectory));
        builder.Services.AddSingleton<IDdsPreviewService, DdsPreviewService>();
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
