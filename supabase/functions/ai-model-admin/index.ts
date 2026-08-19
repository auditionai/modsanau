import { createClient } from "@supabase/supabase-js";

type AnyMap = Record<string, unknown>;
const GPTI2_SIZES = ["1024x1024", "1536x1536", "2048x2048", "1280x720", "2560x1440", "3840x2160", "720x1280", "1440x2560", "2160x3840", "1024x768", "2048x1536", "3200x2400", "768x1024", "1536x2048", "2400x3200", "1536x1024", "2400x1600", "3360x2240", "1024x1536", "1600x2400", "2240x3360", "1280x544", "2560x1088", "3840x1632"];
function gpti2Pricing() {
  return GPTI2_SIZES.flatMap((size) => ["low", "medium", "high"].flatMap((quality) => [1, 2, 3, 4].map((n) => ({ size, quality, n, cost: 50 * n }))));
}
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

function isAllowedImageModel(identity: string) {
  const value = identity.toLowerCase().replace(/[._]/g, " ");
  return /\bgpt(?:[- ]?image)?[- ]?2\b/.test(value)
    || /\bnano[- ]?banana[- ]?pro\b/.test(value);
}

function normalizeModelId(identity: string) {
  const value = identity.toLowerCase().replace(/[._]/g, " ");
  if (/\bnano[- ]?banana[- ]?pro\b/.test(value)) return "nano-banana-pro";
  if (/\bgpt(?:[- ]?image)?[- ]?2\b/.test(value)) return "gpt-image-2";
  return "";
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
  const key = Deno.env.get("GPTI2_API_KEY");
  if (!key) throw new Error("AI_MODEL_PROVIDER_NOT_CONFIGURED");
  const response = await fetch("https://gpti2.store/v1/models", {
    headers: { Authorization: `Bearer ${key}`, Accept: "application/json" },
  });
  const source = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error("AI_MODEL_PROVIDER_UNAVAILABLE");
  const nanoResponse = await fetch("https://gpti2.store/v1/images/nano/models", {
    headers: { Authorization: `Bearer ${key}`, Accept: "application/json" },
  });
  const nanoSource = await nanoResponse.json().catch(() => ({}));
  const rows: unknown[] = [
    ...(Array.isArray(source) ? source : Array.isArray(source.models) ? source.models : []),
    ...(Array.isArray(nanoSource) ? nanoSource : Array.isArray(nanoSource.models) ? nanoSource.models : []),
  ];
  const result = rows.filter((row: unknown) => {
    const item = row as AnyMap;
    const type = String(item.type ?? item.category ?? "").toLowerCase();
    const identity = `${item.id ?? item.slug ?? item.model ?? ""} ${item.name ?? item.title ?? ""}`.toLowerCase();
    return (!type || type === "image" || type.includes("image")) && isAllowedImageModel(identity);
  }).map((row: unknown) => {
    const item = row as AnyMap;
    return {
      id: normalizeModelId(`${item.id ?? item.slug ?? item.model ?? ""} ${item.name ?? item.title ?? ""}`),
      name: normalizeModelId(`${item.id ?? item.slug ?? item.model ?? ""} ${item.name ?? item.title ?? ""}`) === "gpt-image-2" ? "GPT Image 2" : "Nano Banana PRO",
      type: String(item.type ?? item.category ?? "image").toLowerCase(),
      pricing: Array.isArray(item.pricing) && item.pricing.length ? item.pricing : gpti2Pricing(),
      params: normalizeModelId(`${item.id ?? item.slug ?? item.model ?? ""} ${item.name ?? item.title ?? ""}`) === "gpt-image-2"
        ? { size: ["1024x1024", "1536x1536", "2048x2048", "1280x720", "2560x1440", "3840x2160", "720x1280", "1440x2560", "2160x3840", "1024x768", "2048x1536", "3200x2400", "768x1024", "1536x2048", "2400x3200", "1536x1024", "2400x1600", "3360x2240", "1024x1536", "1600x2400", "2240x3360", "1280x544", "2560x1088", "3840x1632"], quality: ["low", "medium", "high"], n: ["1", "2", "3", "4"] }
        : { size: ["1024x1024", "1536x1536", "2048x2048", "1280x720", "2560x1440", "3840x2160", "720x1280", "1440x2560", "2160x3840", "1024x768", "2048x1536", "3200x2400", "768x1024", "1536x2048", "2400x3200", "1536x1024", "2400x1600", "3360x2240", "1024x1536", "1600x2400", "2240x3360", "1280x544", "2560x1088", "3840x1632"], quality: ["low", "medium", "high"], n: ["1", "2", "3", "4"] },
    };
  }).filter((item: { id: string }) => /^[a-z0-9._-]{1,64}$/.test(item.id));
  return [...new Map(result.map((item) => [item.id, item])).values()];
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
    const presetAction = String(body.action ?? "");
    const isPresetAction = ["preset_list", "preset_save", "preset_delete"].includes(presetAction);
    const { data, error } = isPresetAction
      ? await admin.rpc("ai_image_prompt_preset_api", {
        action: presetAction === "preset_list" ? "admin_list" : presetAction === "preset_save" ? "save" : "delete",
        payload: { ...body, adminUserId: user.user.id, recentAuth },
      })
      : await admin.rpc("gpti2_pricing_admin_api", {
        action: req.method === "GET" ? "list" : "save",
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
