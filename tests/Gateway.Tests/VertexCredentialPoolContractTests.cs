namespace Gateway.Tests;

public sealed class VertexCredentialPoolContractTests
{
    [Fact]
    public void Credential_pool_is_vault_only_service_role_selected_and_owner_managed()
    {
        var root = FindRepositoryRoot();
        var sql = File.ReadAllText(Path.Combine(root, "supabase", "migrations",
            "202608160014_vertex_ai_credential_pool.sql"));

        Assert.Contains("private.vertex_ai_credentials", sql, StringComparison.Ordinal);
        Assert.Contains("ENABLE ROW LEVEL SECURITY", sql, StringComparison.Ordinal);
        Assert.Contains("FORCE ROW LEVEL SECURITY", sql, StringComparison.Ordinal);
        Assert.Contains("vault.decrypted_secrets", sql, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE SKIP LOCKED", sql, StringComparison.Ordinal);
        Assert.Contains("last_selected_at NULLS FIRST", sql, StringComparison.Ordinal);
        Assert.Contains("VERTEX_LAST_CREDENTIAL_REQUIRED", sql, StringComparison.Ordinal);
        Assert.Contains("ai_vertex_credential_acquire_api", sql, StringComparison.Ordinal);
        Assert.Contains("ai_vertex_credential_report_api", sql, StringComparison.Ordinal);
        Assert.Contains("AI_SERVICE_ROLE_REQUIRED", sql, StringComparison.Ordinal);
        Assert.Contains("ADMIN_OWNER_REQUIRED", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT SELECT ON private.vertex_ai_credentials TO authenticated", sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Edge_function_retries_only_through_the_credential_pool_and_reports_health()
    {
        var root = FindRepositoryRoot();
        var edge = File.ReadAllText(Path.Combine(root, "supabase", "functions", "gpti2-image", "index.ts"));
        var vertex = File.ReadAllText(Path.Combine(root, "supabase", "functions", "_shared", "vertex-ai.ts"));

        Assert.Contains("ai_vertex_credential_acquire_api", edge, StringComparison.Ordinal);
        Assert.Contains("ai_vertex_credential_report_api", edge, StringComparison.Ordinal);
        Assert.Contains("attempt < 2", edge, StringComparison.Ordinal);
        Assert.Contains("readReferenceImages", edge, StringComparison.Ordinal);
        Assert.Contains("VertexCompositionError", edge, StringComparison.Ordinal);
        Assert.Contains("lastStatus === 429", vertex, StringComparison.Ordinal);
        Assert.Contains("VERTEX_MODEL_FALLBACK", vertex, StringComparison.Ordinal);
        Assert.DoesNotContain("TRAM_SANG_TAO_API_KEY", vertex, StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_pool_surface_never_renders_or_requests_stored_json()
    {
        var root = FindRepositoryRoot();
        var admin = File.ReadAllText(Path.Combine(root, "web", "admin", "vertex-admin.js"));

        Assert.Contains("add_vertex_credential", admin, StringComparison.Ordinal);
        Assert.Contains("set_vertex_credential_enabled", admin, StringComparison.Ordinal);
        Assert.Contains("reset_vertex_credential_cooldown", admin, StringComparison.Ordinal);
        Assert.Contains("retire_vertex_credential", admin, StringComparison.Ordinal);
        Assert.Contains("vertex-credential-list", admin, StringComparison.Ordinal);
        Assert.DoesNotContain("value.credentialsJson", admin, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", admin, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
