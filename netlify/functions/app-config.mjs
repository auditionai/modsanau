const headers = {
  "Content-Type": "application/json; charset=utf-8",
  "Cache-Control": "no-store",
  "X-Content-Type-Options": "nosniff",
};

export default async () => {
  const supabaseUrl = process.env.SUPABASE_URL?.trim();
  const publishableKey = process.env.SUPABASE_PUBLISHABLE_KEY?.trim();
  if (!supabaseUrl || !publishableKey || !/^https:\/\/[a-z0-9-]+\.supabase\.co\/?$/i.test(supabaseUrl)) {
    return new Response(JSON.stringify({ code: "APP_CONFIG_UNAVAILABLE" }), { status: 503, headers });
  }
  return new Response(JSON.stringify({
    supabaseUrl: supabaseUrl.replace(/\/$/, ""),
    publishableKey,
    releaseBucket: "desktop-releases",
    releaseObject: "stable/AuditionAI-Mod-Studio-win-x64.zip",
  }), { status: 200, headers });
};
