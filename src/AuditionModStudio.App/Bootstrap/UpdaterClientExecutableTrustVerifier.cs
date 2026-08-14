using AuditionModStudio.Security;
using AuditionModStudio.Updater;

namespace AuditionModStudio.App.Bootstrap;

internal sealed class UpdaterClientExecutableTrustVerifier(IAuthenticodeUpdateVerifier verifier)
    : IClientExecutableTrustVerifier
{
    public async Task<ClientExecutableTrustResult> VerifyAsync(
        string executablePath,
        string expectedPublisherSubject,
        string expectedPublisherThumbprint,
        CancellationToken cancellationToken = default)
    {
        var result = await verifier.VerifyAsync(
            executablePath,
            expectedPublisherSubject,
            expectedPublisherThumbprint,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? ClientExecutableTrustResult.Success()
            : ClientExecutableTrustResult.Failure(result.DiagnosticCode);
    }
}
