namespace Core.Tests;

public sealed class PrivilegeModelAdrTests
{
    [Fact]
    public void Plan75_adr_has_inventory_migration_broker_uac_and_compatibility_contract()
    {
        var root = FindRepositoryRoot();
        var text = File.ReadAllText(Path.Combine(root, "docs", "ADR", "0002-privilege-model.md"));
        string[] required =
        [
            "requireAdministrator", "asInvoker", "Inventory operation", "LocalApplicationData",
            "Credential Manager", "DPAPI", "ACV Tool 5", "DirectXTex", "user-selected",
            "least-privilege broker", "Protocol đóng", "Path allowlist", "UAC", "uiAccess=false",
            "Upgrade và backward compatibility", "alternate administrator", "Test plan",
            "Rollback", "không đổi manifest", "game installation", "silent self-elevation",
        ];
        foreach (var value in required) Assert.Contains(value, text, StringComparison.OrdinalIgnoreCase);

        var manifest = File.ReadAllText(Path.Combine(root, "src", "AuditionModStudio.App", "app.manifest"));
        Assert.Contains("requireAdministrator", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("asInvoker", manifest, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
