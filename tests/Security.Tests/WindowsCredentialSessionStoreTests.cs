using AuditionModStudio.Core.Auth;
using AuditionModStudio.Security;

namespace Security.Tests;

public sealed class WindowsCredentialSessionStoreTests
{
    [Fact]
    public async Task Oversized_session_is_rejected_before_windows_credential_write()
    {
        using var store = new WindowsCredentialSessionStore();
        var oversized = new AuthSessionSecrets(
            new string('a', 3_000), "refresh", DateTimeOffset.UtcNow.AddHours(1));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(oversized));
    }

    [Fact]
    public void Implementation_does_not_accept_a_plain_json_storage_path()
    {
        var constructors = typeof(WindowsCredentialSessionStore).GetConstructors();

        Assert.Single(constructors);
        Assert.Empty(constructors[0].GetParameters());
        Assert.DoesNotContain(typeof(WindowsCredentialSessionStore).GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public),
            field => field.FieldType == typeof(FileInfo) || field.FieldType == typeof(DirectoryInfo));
    }
}
