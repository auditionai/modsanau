import { createClient } from "@supabase/supabase-js";

const cors = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "GET,POST,OPTIONS",
  "Content-Type": "application/json",
};

const IMAGE_MODELS = new Set([
  "flux-2-pro", "grok-image", "image-4.0", "image-gpt", "image-gpt-2",
  "imagen-4", "imagen-4-fast", "imagen-4-ultra", "kling-o1-image",
  "nano-banana", "nano-banana-2", "nano-banana-pro", "nano-banana-pro-cheap",
  "seedream-4.5", "seedream-5-pro",
]);
const TST_BASE = "https://api.tramsangtao.com/v1";
const MODEL_CACHE_MS = 5 * 60_000;
let modelCache: { expires: number; models: unknown[] } | null = null;

type AnyMap = Record<string, unknown>;

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: cors });
}

function error(code: string, status = 400) {
  return json({ error: code }, status);
}

function authToken(req: Request) {
  const value = req.headers.get("authorization") ?? "";
  return value.startsWith("Bearer ") ? value.slice(7).trim() : "";
}

async function provider(path: string, init: RequestInit = {}) {
  const key = Deno.env.get("TST_API_KEY");
  if (!key) throw new Error("TST_API_KEY_NOT_CONFIGURED");
  const headers = new Headers(init.headers);
  headers.set("Authorization", `Bearer ${key}`);
  headers.set("Accept", "application/json");
  if (init.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  const response = await fetch(`${TST_BASE}${path}`, { ...init, headers });
  const text = await response.text();
  let body: unknown = {};
  try { body = text ? JSON.parse(text) : {}; } catch { body = {}; }
  if (!response.ok) {
    const code = (body as AnyMap)?.error ?? (body as AnyMap)?.message;
    throw new Error(typeof code === "string" ? code : `TST_HTTP_${response.status}`);
  }
  return body as AnyMap;
}

async function models() {
  if (modelCache && modelCache.expires > Date.now()) return modelCache.models;
  const source = await provider("/models");
  const rows = Array.isArray(source) ? source : Array.isArray(source.models) ? source.models : [];
  const filtered = rows.filter((row) => {
    const item = row as AnyMap;
    const id = String(item.id ?? item.slug ?? item.model ?? "");
    const type = String(item.type ?? item.category ?? "").toLowerCase();
    return IMAGE_MODELS.has(id) && (!type || type === "image");
  }).map((row) => {
    const item = row as AnyMap;
    return {
      id: String(item.id ?? item.slug ?? item.model),
      name: item.name ?? item.title ?? item.id,
      type: "image",
      servers: item.servers ?? [],
      pricing: item.pricing ?? [],
      modes: item.modes ?? [],
      params: item.params ?? {},
      notes: item.notes ?? null,
    };
  });
  modelCache = { expires: Date.now() + MODEL_CACHE_MS, models: filtered };
  return filtered;
}

function modelById(catalog: unknown[], id: string): AnyMap | null {
  return (catalog.find((item) => (item as AnyMap).id === id) as AnyMap | undefined) ?? null;
}

function pickSettings(model: AnyMap, requested: AnyMap) {
  const params = model.params && typeof model.params === "object" ? model.params as AnyMap : {};
  const accepted = new Set(Object.keys(params));
  const result: AnyMap = {};
  for (const [key, value] of Object.entries(requested)) {
    if (accepted.has(key) && value !== null && value !== undefined && value !== "") result[key] = value;
  }
  return result;
}

function pricingCost(model: AnyMap, settings: AnyMap): number {
  const rows = Array.isArray(model.pricing) ? model.pricing as AnyMap[] : [];
  const matches = rows.filter((row) => Object.entries(row).every(([key, value]) =>
    ["credits", "key", "config_key"].includes(key) || value === undefined || settings[key] === undefined || String(settings[key]) === String(value)));
  const values = (matches.length ? matches : rows)
    .map((row) => Number(row.credits ?? row.cost ?? 0))
    .filter((value) => Number.isFinite(value) && value > 0);
  return values.length ? Math.min(...values) : 1;
}

function requestHash(value: unknown) {
  return crypto.subtle.digest("SHA-256", new TextEncoder().encode(JSON.stringify(value)))
    .then((buffer) => Array.from(new Uint8Array(buffer)).map((x) => x.toString(16).padStart(2, "0")).join("").toUpperCase());
}

async function rpc(admin: ReturnType<typeof createClient>, action: string, payload: AnyMap) {
  const { data, error: rpcError } = await admin.rpc("ai_image_api", { action, payload });
  if (rpcError) throw new Error(rpcError.message || "AI_DATABASE_ERROR");
  return data as AnyMap;
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response("ok", { headers: cors });
  const token = authToken(req);
  if (!token) return error("AUTH_REQUIRED", 401);
  const supabaseUrl = Deno.env.get("SUPABASE_URL");
  const anonKey = Deno.env.get("SUPABASE_ANON_KEY");
  const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  if (!supabaseUrl || !anonKey || !serviceKey) return error("SUPABASE_CONFIG_INVALID", 500);
  const userClient = createClient(supabaseUrl, anonKey, { global: { headers: { Authorization: `Bearer ${token}` } } });
  const { data: userData, error: userError } = await userClient.auth.getUser(token);
  if (userError || !userData.user) return error("AUTH_REQUIRED", 401);
  const admin = createClient(supabaseUrl, serviceKey);
  const url = new URL(req.url);
  const action = url.searchParams.get("action") ?? "";
  try {
    if (action === "models") return json({ models: await models() });
    if (action === "history") return json(await rpc(admin, "history", { userId: userData.user.id }));
    if (action === "generate") {
      if (req.method !== "POST") return error("METHOD_NOT_ALLOWED", 405);
      const body = await req.json() as AnyMap;
      const modelId = String(body.model ?? "");
      const catalog = await models();
      const model = modelById(catalog, modelId);
      if (!model) return error("AI_MODEL_NOT_ALLOWED", 400);
      const prompt = String(body.prompt ?? "").trim();
      if (!prompt || prompt.length > 16_000) return error("AI_PROMPT_INVALID", 400);
      const settings = pickSettings(model, (body.settings as AnyMap) ?? {});
      const idempotencyKey = String(body.idempotency_key ?? crypto.randomUUID());
      const request = { model: modelId, prompt, settings };
      const hash = await requestHash(request);
      const creditCost = pricingCost(model, settings);
      const prepared = await rpc(admin, "prepare", {
        userId: userData.user.id, model: modelId, settings, requestHash: hash,
        idempotencyKey, creditCost, pricingVersion: "tst-live",
      });
      if (prepared.replayed && prepared.providerJobId) return json({ job_id: prepared.jobId });
      const submitted = await provider("/image/generate", {
        method: "POST",
        body: JSON.stringify({ model: modelId, prompt, ...settings }),
      });
      const providerJobId = String(submitted.job_id ?? submitted.id ?? "");
      const directResult = typeof submitted.result === "string" ? submitted.result : null;
      if (!providerJobId && !directResult) throw new Error("TST_JOB_ID_MISSING");
      await rpc(admin, "submitted", {
        userId: userData.user.id, jobId: prepared.jobId, providerJobId: providerJobId || `direct-${prepared.jobId}`,
      });
      if (directResult) {
        await rpc(admin, "complete", { userId: userData.user.id, jobId: prepared.jobId, resultUrl: directResult });
        return json({ job_id: prepared.jobId, status: "completed", result: directResult });
      }
      return json({ job_id: prepared.jobId, status: "queued" });
    }
    if (action === "status") {
      const jobId = url.searchParams.get("job_id") ?? "";
      if (!/^[0-9a-f-]{36}$/i.test(jobId)) return error("AI_JOB_INVALID", 400);
      const current = await rpc(admin, "status", { userId: userData.user.id, jobId });
      if (current.status === "Completed" || current.status === "Failed") return json({
        job_id: jobId, status: String(current.status).toLowerCase(), result: current.resultUrl ?? null,
      });
      if (!current.providerJobId) return json({ job_id: jobId, status: "queued" });
      const remote = await provider(`/jobs/${encodeURIComponent(current.providerJobId)}`);
      const state = String(remote.status ?? remote.state ?? "processing").toLowerCase();
      const result = typeof remote.result === "string" ? remote.result : typeof remote.output === "string" ? remote.output : null;
      if (result && /^https:\/\/[^\s]+$/.test(result)) {
        const completed = await rpc(admin, "complete", { userId: userData.user.id, jobId, resultUrl: result });
        return json({ job_id: jobId, status: "completed", result: completed.resultUrl ?? result });
      }
      if (["failed", "error", "cancelled"].includes(state)) {
        await rpc(admin, "fail", { userId: userData.user.id, jobId, errorCode: "TST_PROVIDER_FAILED" });
        return json({ job_id: jobId, status: state });
      }
      return json({ job_id: jobId, status: "processing" });
    }
    return error("AI_ACTION_UNKNOWN", 400);
  } catch (caught) {
    const message = caught instanceof Error ? caught.message : "AI_PROVIDER_FAILED";
    return error(message.startsWith("TST_") || message.startsWith("AI_") ? message : "AI_PROVIDER_FAILED", 502);
  }
});
