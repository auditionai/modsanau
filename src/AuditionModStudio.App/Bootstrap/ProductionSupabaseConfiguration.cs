namespace AuditionModStudio.App.Bootstrap;

// Supabase URL and publishable key identify the public client project; they are not secrets.
// Environment variables remain supported for isolated development/test projects.
internal static class ProductionSupabaseConfiguration
{
    internal const string ProjectUrl = "https://plvuutsjwsawkkrmvigz.supabase.co";
    internal const string PublishableKey = "sb_publishable_wSAdCCfFmDGPVOxblESoRQ_1ol-e-PG";

    // ECDSA P-256 public key for verifying device entitlement grants signed by backend
    // Generated: 2026-08-15 PLAN 108
    // Private key stored in Supabase Vault as ECDSA_SIGNING_PRIVATE_KEY
    internal const string EntitlementPublicKeyPem = @"-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEUWdjqhCxBbgr0CEM5OBNYQHM3Zvl
te3MaUWEv5Uv/dn3Vf38RllPI9e7X3Mz71OQeSWgiK5koyhdAuGlI+Jurw==
-----END PUBLIC KEY-----";

    internal static (string Url, string PublishableKey) Resolve() =>
        (Environment.GetEnvironmentVariable("AUDITION_SUPABASE_URL")?.Trim() ?? ProjectUrl,
         Environment.GetEnvironmentVariable("AUDITION_SUPABASE_PUBLISHABLE_KEY")?.Trim() ?? PublishableKey);
}
