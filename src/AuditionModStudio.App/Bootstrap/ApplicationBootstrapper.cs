using AuditionModStudio.Archives;
using AuditionModStudio.AI;
using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Accounts;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Auth;
using AuditionModStudio.App.Authentication;
using AuditionModStudio.Core.Catalog;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Diagnostics;
using AuditionModStudio.Core.Exports;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Startup;
using AuditionModStudio.Core.Settings;
using AuditionModStudio.Core.Tasks;
using AuditionModStudio.Core.Subscriptions;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Infrastructure.Logging;
using AuditionModStudio.Infrastructure.Exports;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Infrastructure.Startup;
using AuditionModStudio.Infrastructure.Settings;
using AuditionModStudio.Infrastructure.Tasks;
using AuditionModStudio.Infrastructure.Workspaces;
using AuditionModStudio.Imaging;
using AuditionModStudio.Mods;
using AuditionModStudio.Projects;
using AuditionModStudio.Cloud;
using AuditionModStudio.Security;
using AuditionModStudio.Dds;
using AuditionModStudio.App.Shell;
using AuditionModStudio.App.Home;
using AuditionModStudio.Updater;
using AuditionModStudio.App.Editor;
using AuditionModStudio.App.Workspace;
using AuditionModStudio.App.AiStudio;
using AuditionModStudio.App.Account;
using AuditionModStudio.App.Settings;
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
            ContentRootPath = AppContext.BaseDirectory,
        });

        _logSession.ConfigureServices(builder.Services);
        builder.Services.AddSingleton<ISensitiveDataRedactor>(_logSession.Redactor);
        builder.Services.AddSingleton<IAppPaths>(paths);
        builder.Services.AddSingleton<IPathSecurity>(pathSecurity);
        builder.Services.AddSingleton<IExportDestinationFileSystem, SystemExportDestinationFileSystem>();
        builder.Services.AddSingleton<IArchiveExportDestinationValidator, ArchiveExportDestinationValidator>();
        builder.Services.AddSingleton<IArchiveExportFileOperations, SystemArchiveExportFileOperations>();
        builder.Services.AddSingleton<IArchiveExportService, ArchiveExportService>();
        builder.Services.AddSingleton<IDiagnosticExportService, DiagnosticExportService>();
        builder.Services.AddSingleton(BatchBuildExportOptions.Default);
        builder.Services.AddSingleton<IBatchBuildExportService, BatchBuildExportService>();
        builder.Services.AddSingleton<SecureWorkspaceService>();
        builder.Services.AddSingleton<ISecureWorkspaceService>(services =>
            services.GetRequiredService<SecureWorkspaceService>());
        builder.Services.AddSingleton<ISecureWorkspaceRecoveryService>(services =>
            services.GetRequiredService<SecureWorkspaceService>());
        builder.Services.AddSingleton<ISecureWorkspaceRetentionService>(services =>
            services.GetRequiredService<SecureWorkspaceService>());
        builder.Services.AddSingleton<ISecureWorkspaceRemovalService>(services =>
            services.GetRequiredService<SecureWorkspaceService>());
        builder.Services.AddSingleton<IWorkspaceCrashRecoveryService>(services =>
            services.GetRequiredService<SecureWorkspaceService>());
        builder.Services.AddHostedService(services =>
            services.GetRequiredService<SecureWorkspaceService>());
        builder.Services.AddSingleton<ISettingsValidator, SettingsValidator>();
        builder.Services.AddSingleton<IAtomicSettingsWriter, AtomicSettingsWriter>();
        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddHostedService<SettingsInitializationService>();
        builder.Services.AddSingleton(TrustedArchiveToolManifest.Production);
        builder.Services.AddSingleton<ArchiveToolIntegrityPolicy>();
        builder.Services.AddSingleton(ProjectArchiveToolIntegrityOptions.CreateProduction(AppContext.BaseDirectory));
        builder.Services.AddSingleton<IProjectToolIntegrityValidator, ProjectArchiveToolIntegrityValidator>();
        builder.Services.AddSingleton<IWorkspaceProtection, WindowsWorkspaceProtection>();
        builder.Services.AddSingleton<IArchiveToolExecutionPolicy>(services =>
            services.GetRequiredService<ArchiveToolIntegrityPolicy>());
        builder.Services.AddSingleton<IKeydatService, KeydatService>();
        builder.Services.AddSingleton<IArchiveToolProvisioningService, ArchiveToolProvisioningService>();
        builder.Services.AddSingleton<IArchiveToolRunner, AcvTool5Runner>();
        builder.Services.AddSingleton<IGameRegionProfileResolver, GameRegionProfileCatalog>();
        builder.Services.AddSingleton(new AcvTool5ArchiveEngineOptions(AppContext.BaseDirectory, "acv.exe"));
        builder.Services.AddSingleton<IArchiveEngine, AcvTool5ArchiveEngine>();
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
        builder.Services.AddSingleton<IAiMaskEditingService, AiMaskEditingService>();
        builder.Services.AddSingleton<IAiTransportImageEncoder, AiTransportImageEncoder>();
        builder.Services.AddSingleton<IAiMaskAssetStore, AiMaskAssetStore>();
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
        builder.Services.AddSingleton(ThumbnailCacheOptions.Default);
        builder.Services.AddSingleton<IThumbnailCache, ThumbnailCache>();
        builder.Services.AddSingleton<ITextureLazyLoadingService, TextureLazyLoadingService>();
        builder.Services.AddSingleton<ISmartModScanService, SmartModScanService>();
        builder.Services.AddSingleton<IProjectCreationService, ProjectCreationService>();
        builder.Services.AddSingleton<IProjectLoadService, ProjectLoadService>();
        builder.Services.AddSingleton<IProjectValidator, ProjectValidator>();
        builder.Services.AddSingleton(ProjectBuildOptions.Default);
        builder.Services.AddSingleton<IProjectBuildService, ProjectBuildService>();
        builder.Services.AddSingleton<ITextureBatchBuildSummaryService, TextureBatchBuildSummaryService>();
        builder.Services.AddSingleton<ILocalPromptPresetStore, LocalPromptPresetStore>();
        builder.Services.AddSingleton<ICloudPromptPresetService, UnavailableCloudPromptPresetService>();
        builder.Services.AddSingleton<IAiService, UnavailableAiService>();
        builder.Services.AddSingleton<ISecureSessionStore, WindowsCredentialSessionStore>();
        builder.Services.AddSingleton<IDeviceSessionBindingStore, WindowsCredentialDeviceSessionBindingStore>();
        builder.Services.AddSingleton<IDeviceEntitlementGrantStore, WindowsCredentialDeviceEntitlementGrantStore>();
        builder.Services.AddSingleton<ICapabilityAuthorizationService>(_ =>
            new CapabilityAuthorizationService(TimeProvider.System));
        builder.Services.AddSingleton<ITemplateCacheKeyProtector, WindowsDpapiTemplateCacheKeyProtector>();
        builder.Services.AddSingleton<IPremiumTemplateCache, EncryptedPremiumTemplateCache>();
        builder.Services.AddSingleton(new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        });
        builder.Services.AddSingleton<IAuthenticationService>(services =>
        {
            var (url, publishableKey) = ProductionSupabaseConfiguration.Resolve();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var projectUri)
                || string.IsNullOrWhiteSpace(publishableKey))
            {
                return new UnavailableAuthenticationService();
            }

            var options = new SupabaseAuthOptions(projectUri, publishableKey);
            return options.IsValid
                ? new SupabaseAuthService(
                    services.GetRequiredService<HttpClient>(),
                    services.GetRequiredService<ISecureSessionStore>(),
                    options)
                : new UnavailableAuthenticationService();
        });
        builder.Services.AddSingleton<IDesktopAccessService>(services =>
        {
            var (url, publishableKey) = ProductionSupabaseConfiguration.Resolve();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var projectUri) || string.IsNullOrWhiteSpace(publishableKey))
                return new UnavailableDesktopAccessService();
            var options = new SupabaseAuthOptions(projectUri, publishableKey);
            return options.IsValid
                ? new SupabaseDesktopAccessService(
                    services.GetRequiredService<HttpClient>(),
                    services.GetRequiredService<ISecureSessionStore>(),
                    services.GetRequiredService<IDeviceSessionBindingStore>(),
                    options)
                : new UnavailableDesktopAccessService();
        });
        builder.Services.AddSingleton<IAiStudioService>(services =>
        {
            var url = Environment.GetEnvironmentVariable("AUDITION_GATEWAY_URL");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var gatewayUri))
            {
                return new UnavailableAiStudioService();
            }
            var deviceSessionsEnabled = bool.TryParse(
                Environment.GetEnvironmentVariable("AUDITION_DEVICE_SESSIONS_ENABLED"), out var enabled) && enabled;
            var gatewayOptions = new GatewayAiStudioOptions(gatewayUri, deviceSessionsEnabled);
            return gatewayOptions.IsValid
                ? new GatewayAiStudioService(
                    services.GetRequiredService<HttpClient>(),
                    services.GetRequiredService<ISecureSessionStore>(),
                    services.GetRequiredService<IAuthenticationService>(),
                    gatewayOptions,
                    services.GetRequiredService<IAiTransportImageEncoder>(),
                    services.GetRequiredService<IImageImportService>(),
                    services.GetRequiredService<IDeviceSessionBindingStore>())
                : new UnavailableAiStudioService();
        });
        builder.Services.AddSingleton<IProductCatalogService>(services =>
        {
            var url = Environment.GetEnvironmentVariable("AUDITION_GATEWAY_URL");
            var keyId = Environment.GetEnvironmentVariable("AUDITION_PRODUCT_CATALOG_KEY_ID");
            var publicKey = Environment.GetEnvironmentVariable("AUDITION_PRODUCT_CATALOG_PUBLIC_KEY_PEM");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var gatewayUri)
                || string.IsNullOrWhiteSpace(keyId) || keyId.Length > 64
                || string.IsNullOrWhiteSpace(publicKey) || publicKey.Length > 16_384)
                return new UnavailableProductCatalogService();
            var catalogOptions = new ProductCatalogOptions(gatewayUri,
                paths.CacheDirectory,
                System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add(keyId, publicKey));
            return catalogOptions.IsValid
                ? new ProductCatalogService(services.GetRequiredService<HttpClient>(),
                    services.GetRequiredService<ISecureSessionStore>(), catalogOptions,
                    services.GetRequiredService<IPathSecurity>())
                : new UnavailableProductCatalogService();
        });
        builder.Services.AddSingleton<IAccountOverviewService>(services =>
        {
            var url = Environment.GetEnvironmentVariable("AUDITION_GATEWAY_URL");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var gatewayUri))
                return new UnavailableAccountOverviewService();
            var accountOptions = new GatewayAccountOptions(gatewayUri);
            return accountOptions.IsValid
                ? new GatewayAccountOverviewService(services.GetRequiredService<HttpClient>(),
                    services.GetRequiredService<ISecureSessionStore>(),
                    services.GetRequiredService<IAuthenticationService>(), accountOptions)
                : new UnavailableAccountOverviewService();
        });
        builder.Services.AddSingleton<IDeviceEntitlementService>(services =>
        {
            var url = Environment.GetEnvironmentVariable("AUDITION_GATEWAY_URL");
            var publicKey = Environment.GetEnvironmentVariable("AUDITION_ENTITLEMENT_PUBLIC_KEY_PEM");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var gatewayUri) || string.IsNullOrWhiteSpace(publicKey))
                return new UnavailableDeviceEntitlementService();
            var entitlementOptions = new GatewayDeviceEntitlementOptions(gatewayUri, publicKey);
            return entitlementOptions.IsValid
                ? new GatewayDeviceEntitlementService(services.GetRequiredService<HttpClient>(),
                    services.GetRequiredService<ISecureSessionStore>(),
                    services.GetRequiredService<IDeviceSessionBindingStore>(),
                    services.GetRequiredService<IDeviceEntitlementGrantStore>(),
                    services.GetRequiredService<IAuthenticationService>(),
                    services.GetRequiredService<ICapabilityAuthorizationService>(), entitlementOptions)
                : new UnavailableDeviceEntitlementService();
        });
        builder.Services.AddSingleton<ITextureStateMachine, TextureStateMachine>();
        builder.Services.AddSingleton<IProjectTextureRestoreService, ProjectTextureRestoreService>();
        builder.Services.AddSingleton<IProjectResetService, ProjectResetService>();
        builder.Services.AddSingleton<ITextureApplyService, TextureApplyService>();
        // Production update signing keys/feed/CDN are intentionally not configured in source.
        // Local editing/build/export remains available while the updater fails closed.
        builder.Services.AddSingleton<IAppUpdateService, UnavailableAppUpdateService>();
        // Production endpoint/public trust root chưa được Product Owner cung cấp: fail closed, không dùng env làm trust root.
#if PLAN102_UI_EVIDENCE
        builder.Services.AddSingleton<IPortableUpdateCoordinator, Plan102UiEvidenceUpdateCoordinator>();
#else
        builder.Services.AddSingleton<IPortableUpdateCoordinator, UnavailablePortableUpdateCoordinator>();
#endif
        builder.Services.AddSingleton<IUserActivityService, UserActivityService>();
        builder.Services.AddSingleton(BackgroundTaskManagerOptions.Default);
        builder.Services.AddSingleton<BackgroundTaskManager>();
        builder.Services.AddSingleton<IBackgroundTaskManager>(services =>
            services.GetRequiredService<BackgroundTaskManager>());
        builder.Services.AddHostedService(services =>
            services.GetRequiredService<BackgroundTaskManager>());
        builder.Services.AddSingleton<IStartupValidator, StartupValidator>();
        builder.Services.AddSingleton<ApplicationProjectSession>();
        builder.Services.AddSingleton<IApplicationProjectSession>(services =>
            services.GetRequiredService<ApplicationProjectSession>());
        builder.Services.AddSingleton<HomeViewModel>();
        builder.Services.AddTransient<HomePage>();
        builder.Services.AddSingleton<ProjectWorkspaceViewModel>();
        builder.Services.AddSingleton<BuildExportViewModel>();
        builder.Services.AddSingleton<IWorkspaceTextureSelection>(services =>
            services.GetRequiredService<ProjectWorkspaceViewModel>());
        builder.Services.AddTransient<ProjectWorkspacePage>();
        builder.Services.AddSingleton<ImageEditorViewModel>();
        builder.Services.AddTransient<ImageEditorPage>();
        builder.Services.AddSingleton<AiStudioViewModel>();
        builder.Services.AddSingleton<AiMaskEditorViewModel>();
        builder.Services.AddTransient<AiStudioPage>();
        builder.Services.AddSingleton<AccountViewModel>();
        builder.Services.AddTransient<AccountPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddSingleton<AppShellViewModel>();
        builder.Services.AddTransient<MainPage>();
        builder.Services.AddTransient<LoginPage>();
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
