namespace Gateway.Tests;

public sealed class Plan104MigrationContractTests
{
    [Fact]
    public void Migration_is_forward_only_rls_hardened_and_reuses_credit_authority()
    {
        var root = FindRepositoryRoot();
        var sql = File.ReadAllText(Path.Combine(root, "supabase", "migrations",
            "202608140001_plan104_device_entitlements.sql"));
        Assert.Contains("private.device_profiles", sql, StringComparison.Ordinal);
        Assert.Contains("private.subscriptions", sql, StringComparison.Ordinal);
        Assert.Contains("private.gift_code_redemptions", sql, StringComparison.Ordinal);
        Assert.Contains("ENABLE ROW LEVEL SECURITY", sql, StringComparison.Ordinal);
        Assert.Contains("FORCE ROW LEVEL SECURITY", sql, StringComparison.Ordinal);
        Assert.Contains("private.credit_grant", sql, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service_role", File.ReadAllText(Path.Combine(root, "src",
            "AuditionModStudio.App", "Bootstrap", "ApplicationBootstrapper.cs")), StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
