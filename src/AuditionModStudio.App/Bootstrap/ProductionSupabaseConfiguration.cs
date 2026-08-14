namespace AuditionModStudio.App.Bootstrap;

// Supabase URL and publishable key identify the public client project; they are not secrets.
// Environment variables remain supported for isolated development/test projects.
internal static class ProductionSupabaseConfiguration
{
    internal const string ProjectUrl = "https://plvuutsjwsawkkrmvigz.supabase.co";
    internal const string PublishableKey = "sb_publishable_wSAdCCfFmDGPVOxblESoRQ_1ol-e-PG";

    internal static (string Url, string PublishableKey) Resolve() =>
        (Environment.GetEnvironmentVariable("AUDITION_SUPABASE_URL")?.Trim() ?? ProjectUrl,
         Environment.GetEnvironmentVariable("AUDITION_SUPABASE_PUBLISHABLE_KEY")?.Trim() ?? PublishableKey);
}
