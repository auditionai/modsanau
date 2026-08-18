import { createClient } from "@supabase/supabase-js";

type AnyMap = Record<string, unknown>;
const maxBodyBytes = 16 * 1024;

function allowedOrigin(req: Request) {
  const origin = req.headers.get("origin") ?? "";
  const configured = (Deno.env.get("PAYMENT_ALLOWED_ORIGINS") ?? "").split(",").map((value) => value.trim()).filter(Boolean);
  return configured.includes(origin) || /^https:\/\/[a-z0-9-]+--aumodstudio\.netlify\.app$/i.test(origin) ? origin : "";
}

function headers(req: Request) {
  const origin = allowedOrigin(req);
  return {
    ...(origin ? { "Access-Control-Allow-Origin": origin, Vary: "Origin" } : {}),
    "Access-Control-Allow-Headers": "authorization, apikey, content-type, x-csrf-token",
    "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
    "Content-Type": "application/json",
    "Cache-Control": "no-store",
  };
}

function respond(req: Request, body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: headers(req) });
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

async function providerModels() {
  const key = Deno.env.get("TST_API_KEY") ?? Deno.env.get("TRAM_SANG_TAO_API_KEY");
  if (!key) throw new Error("AI_MODEL_PROVIDER_NOT_CONFIGURED");
  const response = await fetch("https://api.tramsangtao.com/v1/models", {
    headers: { Authorization: `Bearer ${key}`, Accept: "application/json" },
  });
  const source = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error("AI_MODEL_PROVIDER_UNAVAILABLE");
  const rows: unknown[] = Array.isArray(source) ? source : Array.isArray(source.models) ? source.models : [];
  return rows.filter((row: unknown) => {
    const item = row as AnyMap;
    const type = String(item.type ?? item.category ?? "").toLowerCase();
    return type === "image" || type.includes("image");
  }).map((row: unknown) => {
    const item = row as AnyMap;
    return {
      id: String(item.id ?? item.slug ?? item.model ?? "").toLowerCase(),
      name: item.name ?? item.title ?? item.id,
      type: String(item.type ?? item.category ?? "image").toLowerCase(),
      pricing: Array.isArray(item.pricing) ? item.pricing : [],
    };
  }).filter((item: { id: string }) => /^[a-z0-9._-]{1,64}$/.test(item.id));
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { status: 204, headers: headers(req) });
  const accessToken = token(req);
  const supabaseUrl = Deno.env.get("SUPABASE_URL");
  const anonKey = Deno.env.get("SUPABASE_ANON_KEY");
  const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  if (!accessToken || !supabaseUrl || !anonKey || !serviceKey) return respond(req, { error: "ADMIN_AUTH_REQUIRED" }, 401);

  try {
    const userClient = createClient(supabaseUrl, anonKey, {
      auth: { persistSession: false }, global: { headers: { Authorization: `Bearer ${accessToken}` } },
    });
    const { data: user, error: userError } = await userClient.auth.getUser(accessToken);
    if (userError || !user.user) return respond(req, { error: "ADMIN_AUTH_REQUIRED" }, 401);
    const lastSignInAt = Date.parse(user.user.last_sign_in_at ?? "");
    const recentAuth = Number.isFinite(lastSignInAt) && Date.now() - lastSignInAt <= 15 * 60_000;
    const admin = createClient(supabaseUrl, serviceKey, { auth: { persistSession: false } });
    const body = req.method === "POST" ? await readJson(req) : {};
    const action = req.method === "GET" ? "admin_list" : "admin_save";
    if (req.method === "GET") {
      const models = await providerModels();
      const synced = await admin.rpc("ai_model_catalog_api", { action: "sync", payload: { models } });
      if (synced.error) throw new Error(synced.error.message || "AI_MODEL_DATABASE_ERROR");
    }
    const { data, error } = await admin.rpc("ai_model_catalog_api", {
      action,
      payload: { ...body, adminUserId: user.user.id, recentAuth },
    });
    if (error) throw new Error(error.message || "AI_MODEL_DATABASE_ERROR");
    return respond(req, req.method === "GET" ? { models: data } : data);
  } catch (caught) {
    const message = caught instanceof Error ? caught.message : "AI_MODEL_REQUEST_FAILED";
    const known = message.match(/AI_MODEL_[A-Z_]+|ADMIN_[A-Z_]+/)?.[0] ?? "AI_MODEL_REQUEST_FAILED";
    return respond(req, { error: known }, known.startsWith("ADMIN_") ? 403 : 400);
  }
});
