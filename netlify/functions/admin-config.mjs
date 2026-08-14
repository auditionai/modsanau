const jsonHeaders = {
  "Content-Type": "application/json; charset=utf-8",
  "Cache-Control": "no-store",
  "X-Content-Type-Options": "nosniff",
};

export default async () => {
  const supabaseUrl = process.env.SUPABASE_URL?.trim();
  const publishableKey = process.env.SUPABASE_PUBLISHABLE_KEY?.trim();
  if (!supabaseUrl || !publishableKey || !/^https:\/\/[a-z0-9-]+\.supabase\.co\/?$/i.test(supabaseUrl)) {
    return new Response(JSON.stringify({ code: "ADMIN_CONFIG_UNAVAILABLE" }), { status: 503, headers: jsonHeaders });
  }
  return new Response(JSON.stringify({ supabaseUrl: supabaseUrl.replace(/\/$/, ""), publishableKey }), {
    status: 200,
    headers: jsonHeaders,
  });
};
