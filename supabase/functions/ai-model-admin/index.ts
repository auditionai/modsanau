import { createClient } from "@supabase/supabase-js";

type AnyMap = Record<string, unknown>;
const maxBodyBytes = 16 * 1024;

function headers() {
  return {
    "Access-Control-Allow-Headers": "authorization, apikey, content-type, x-csrf-token",
    "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
    "Content-Type": "application/json",
    "Cache-Control": "no-store",
  };
}

function respond(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: headers() });
}

function token(req: Request) {
  const value = req.headers.get("authorization") ?? "";
  return value.startsWith("Bearer ") ? value.slice(7).trim() : "";
}

async function readJson(req: Request): Promise<AnyMap> {
  const declared = Number(req.headers.get("content-length") ?? "0");
  if (declared > maxBodyBytes) throw new Error("AI_MODEL_PAYLOAD_TOO_LARGE");
  const bytes = new Uint8Array(await req.arrayBuffer());
  if (!bytes.length || bytes.length > maxBodyBytes) throw new Error("AI_MODEL_PAYLOAD_INVALID");
  const body = JSON.parse(new TextDecoder().decode(bytes));
  if (!body || typeof body !== "object" || Array.isArray(body)) throw new Error("AI_MODEL_PAYLOAD_INVALID");
  return body as AnyMap;
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { status: 204, headers: headers() });
  const accessToken = token(req);
  const supabaseUrl = Deno.env.get("SUPABASE_URL");
  const anonKey = Deno.env.get("SUPABASE_ANON_KEY");
  const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  if (!accessToken || !supabaseUrl || !anonKey || !serviceKey) return respond({ error: "ADMIN_AUTH_REQUIRED" }, 401);

  try {
    const userClient = createClient(supabaseUrl, anonKey, {
      auth: { persistSession: false }, global: { headers: { Authorization: `Bearer ${accessToken}` } },
    });
    const { data: user, error: userError } = await userClient.auth.getUser(accessToken);
    if (userError || !user.user) return respond({ error: "ADMIN_AUTH_REQUIRED" }, 401);
    const lastSignInAt = Date.parse(user.user.last_sign_in_at ?? "");
    const recentAuth = Number.isFinite(lastSignInAt) && Date.now() - lastSignInAt <= 15 * 60_000;
    const admin = createClient(supabaseUrl, serviceKey, { auth: { persistSession: false } });
    const body = req.method === "POST" ? await readJson(req) : {};
    const action = req.method === "GET" ? "admin_list" : "admin_save";
    const { data, error } = await admin.rpc("ai_model_catalog_api", {
      action,
      payload: { ...body, adminUserId: user.user.id, recentAuth },
    });
    if (error) throw new Error(error.message || "AI_MODEL_DATABASE_ERROR");
    return respond(req.method === "GET" ? { models: data } : data);
  } catch (caught) {
    const message = caught instanceof Error ? caught.message : "AI_MODEL_REQUEST_FAILED";
    const known = message.match(/AI_MODEL_[A-Z_]+|ADMIN_[A-Z_]+/)?.[0] ?? "AI_MODEL_REQUEST_FAILED";
    return respond({ error: known }, known.startsWith("ADMIN_") ? 403 : 400);
  }
});
