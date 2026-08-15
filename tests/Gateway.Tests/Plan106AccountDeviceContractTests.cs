namespace Gateway.Tests;

public sealed class Plan106AccountDeviceContractTests
{
    [Fact]
    public void Desktop_rpc_derives_identity_from_jwt_and_is_not_executable_by_anon()
    {
        var sql = File.ReadAllText(MigrationPath());

        Assert.Contains("actor_id uuid := auth.uid()", sql, StringComparison.Ordinal);
        Assert.Contains("SECURITY DEFINER", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL ON FUNCTION public.desktop_access_api(text, jsonb) FROM PUBLIC, anon", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT EXECUTE ON FUNCTION public.desktop_access_api(text, jsonb) TO authenticated", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("payload->>'userId'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_bucket_is_private_and_download_requires_confirmed_email()
    {
        var sql = File.ReadAllText(MigrationPath());

        Assert.Contains("'desktop-releases', 'desktop-releases', false", sql, StringComparison.Ordinal);
        Assert.Contains("email_confirmed_at IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("name = 'stable/AuditionAI-Mod-Studio-win-x64.zip'", sql, StringComparison.Ordinal);
        Assert.Contains("TO authenticated", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Device_gate_checks_user_device_and_commercial_status()
    {
        var sql = File.ReadAllText(MigrationPath());

        Assert.Contains("private.device_session_register", sql, StringComparison.Ordinal);
        Assert.Contains("private.device_session_validate", sql, StringComparison.Ordinal);
        Assert.Contains("private.device_profile_register", sql, StringComparison.Ordinal);
        Assert.Contains("USER_INACTIVE", sql, StringComparison.Ordinal);
        Assert.Contains("DEVICE_INACTIVE", sql, StringComparison.Ordinal);
    }

    private static string MigrationPath() => Path.Combine(RepositoryRoot(), "supabase", "migrations",
        "202608150003_plan106_public_auth_device_download.sql");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
