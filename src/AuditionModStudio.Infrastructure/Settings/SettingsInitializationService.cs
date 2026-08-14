using AuditionModStudio.Core.Settings;
using Microsoft.Extensions.Hosting;

namespace AuditionModStudio.Infrastructure.Settings;

public sealed class SettingsInitializationService : IHostedService
{
    private readonly ISettingsService _settingsService;

    public SettingsInitializationService(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
