namespace Gateway.Tests;

public sealed class Plan105AdminPortalContractTests
{
    [Fact]
    public void Admin_migration_is_service_role_only_and_audited()
    {
        var root = FindRepositoryRoot();
        var sql = File.ReadAllText(Path.Combine(root, "supabase", "migrations",
            "202608140002_plan105_admin_portal.sql"));

        Assert.Contains("private.admin_users", sql, StringComparison.Ordinal);
        Assert.Contains("private.admin_audit_events", sql, StringComparison.Ordinal);
        Assert.Contains("ENABLE ROW LEVEL SECURITY", sql, StringComparison.Ordinal);
        Assert.Contains("FORCE ROW LEVEL SECURITY", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL ON", sql, StringComparison.Ordinal);
        Assert.Contains("TO service_role", sql, StringComparison.Ordinal);
        Assert.Contains("private.admin_bootstrap_first_user", sql, StringComparison.Ordinal);
        Assert.Contains("private.admin_audit_record", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT SELECT ON private.admin_users TO authenticated", sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gateway_admin_endpoints_are_authenticated_limited_and_registered()
    {
        var root = FindRepositoryRoot();
        var endpoints = File.ReadAllText(Path.Combine(root, "src", "AuditionModStudio.Gateway",
            "Endpoints", "AdminPortalEndpoints.cs"));
        var application = File.ReadAllText(Path.Combine(root, "src", "AuditionModStudio.Gateway",
            "GatewayApplication.cs"));

        Assert.Contains("app.MapAdminPortalEndpoints()", application, StringComparison.Ordinal);
        Assert.Contains("AdminPortalOptions.FromConfiguration", application, StringComparison.Ordinal);
        Assert.Contains("IAdminPortalService", application, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowAnonymous", endpoints, StringComparison.OrdinalIgnoreCase);
        Assert.True(endpoints.Split("/v1/admin/").Length >= 10);
        Assert.True(endpoints.Split(".RequireAuthorization()").Length >= 10);
        Assert.True(endpoints.Split("RequireRateLimiting").Length >= 10);
        Assert.Contains("X-Admin-Reason", endpoints, StringComparison.Ordinal);
        Assert.Contains("X-Admin-Correlation-Id", endpoints, StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_service_keeps_authority_on_server_and_records_audit()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "src", "AuditionModStudio.Gateway",
            "Services", "AdminPortal.cs"));
        var appBootstrapper = File.ReadAllText(Path.Combine(root, "src", "AuditionModStudio.App",
            "Bootstrap", "ApplicationBootstrapper.cs"));

        Assert.Contains("private.credit_grant", service, StringComparison.Ordinal);
        Assert.Contains("private.admin_audit_record", service, StringComparison.Ordinal);
        Assert.Contains("private.admin_bootstrap_first_user", service, StringComparison.Ordinal);
        Assert.Contains("EnsureAdminConnectionAsync", service, StringComparison.Ordinal);
        Assert.Contains("EnsureMutationConnectionAsync", service, StringComparison.Ordinal);
        Assert.Contains("role is not (\"owner\" or \"operator\")", service, StringComparison.Ordinal);
        Assert.Contains("IsAdminAsync", service, StringComparison.Ordinal);
        Assert.Contains("NormalizeReason", service, StringComparison.Ordinal);
        Assert.DoesNotContain("service_role", appBootstrapper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StripeSecret", service, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SupabaseServiceRoleKey", service, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Admin_static_portal_is_noindex_csp_guarded_and_gateway_wired()
    {
        var root = FindRepositoryRoot();
        var adminRoot = Path.Combine(root, "admin-release");
        var index = File.ReadAllText(Path.Combine(adminRoot, "web", "index.html"));
        var script = File.ReadAllText(Path.Combine(adminRoot, "web", "script.js"));
        var headers = File.ReadAllText(Path.Combine(adminRoot, "web", "_headers"));
        var manifest = File.ReadAllText(Path.Combine(adminRoot, "public-allowlist.json"));

        Assert.Contains("noindex", index, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Security-Policy:", headers, StringComparison.Ordinal);
        Assert.Contains("X-Robots-Tag: noindex", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("connect-src 'self'", headers, StringComparison.Ordinal);
        Assert.Contains("/v1/admin/session/login", script, StringComparison.Ordinal);
        Assert.Contains("/v1/admin/dashboard", script, StringComparison.Ordinal);
        Assert.Contains("/v1/admin/analytics", script, StringComparison.Ordinal);
        Assert.Contains("/v1/admin/users", script, StringComparison.Ordinal);
        Assert.Contains("/v1/admin/transactions", script, StringComparison.Ordinal);
        Assert.Contains("/v1/admin/devices", script, StringComparison.Ordinal);
        Assert.Contains("/v1/admin/gift-codes", script, StringComparison.Ordinal);
        Assert.Contains("/v1/admin/audit", script, StringComparison.Ordinal);
        Assert.Contains("AbortController", script, StringComparison.Ordinal);
        Assert.Contains("X-CSRF-Token", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", script, StringComparison.Ordinal);
        Assert.DoesNotContain("data-api-base", index, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"policy\": \"deny-by-default\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_v2_session_and_operations_are_fail_closed()
    {
        var root = FindRepositoryRoot();
        var session = File.ReadAllText(Path.Combine(root, "src", "AuditionModStudio.Gateway",
            "Endpoints", "AdminSessionEndpoints.cs"));
        var csrf = File.ReadAllText(Path.Combine(root, "src", "AuditionModStudio.Gateway",
            "Security", "AdminCsrfMiddleware.cs"));
        var operations = File.ReadAllText(Path.Combine(root, "src", "AuditionModStudio.Gateway",
            "Services", "AdminOperations.cs"));
        var migration = File.ReadAllText(Path.Combine(root, "supabase", "migrations",
            "202608150001_admin_portal_v2.sql"));

        Assert.Contains("__Host-aams-admin", File.ReadAllText(Path.Combine(root, "src",
            "AuditionModStudio.Gateway", "GatewayApplication.cs")), StringComparison.Ordinal);
        Assert.Contains("ADMIN_LOGIN_REJECTED", session, StringComparison.Ordinal);
        Assert.Contains("RandomNumberGenerator.GetBytes", session, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.FixedTimeEquals", csrf, StringComparison.Ordinal);
        Assert.Contains("role==\"auditor\"", operations.Replace(" ", string.Empty), StringComparison.Ordinal);
        Assert.Contains("IsolationLevel.Serializable", operations, StringComparison.Ordinal);
        Assert.Contains("FORCE ROW LEVEL SECURITY", migration, StringComparison.Ordinal);
        Assert.Contains("ON DELETE RESTRICT", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("TO authenticated", migration, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the solution root.");
    }
}
