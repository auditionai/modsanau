import { createClient } from "@supabase/supabase-js";
import { composeWithVertex, VertexCompositionError, type VertexCredentialOutcome } from "../_shared/vertex-ai.ts";

const cors = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "GET,POST,OPTIONS",
  "Content-Type": "application/json",
};

const TST_BASE = "https://api.tramsangtao.com/v1";
const MODEL_CACHE_MS = 5 * 60_000;
let modelCache: { expires: number; models: unknown[] } | null = null;

type AnyMap = Record<string, unknown>;

function isAllowedImageModel(identity: string) {
  const value = identity.toLowerCase().replace(/[._]/g, " ");
  return /\bgpt(?:[- ]?image)?[- ]?2\b/.test(value)
    || /\bnano[- ]?banana[- ]?pro\b/.test(value)
    || /\b(?:image|imagen)[- ]?4\b/.test(value)
    || /\bflux[- ]?2[- ]?pro\b/.test(value);
}

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
  const key = Deno.env.get("TST_API_KEY") ?? Deno.env.get("TRAM_SANG_TAO_API_KEY");
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

function modelParams(item: AnyMap): AnyMap {
  const params: AnyMap = {};
  const keyAliases: Record<string, string> = {
    image_quality: "quality", output_quality: "quality", image_resolution: "resolution",
    output_resolution: "resolution", output_size: "size", image_size: "size",
    aspectRatio: "aspect_ratio", processingSpeed: "processing_speed", num_images: "count",
  };
  const knownKeys = new Set(["quality", "aspect_ratio", "resolution", "size", "speed", "processing_speed", "count", "quantity", "server", "server_id"]);
  const optionValues = (value: unknown): unknown => {
    if (Array.isArray(value)) return value;
    if (value && typeof value === "object" && !Array.isArray(value)) {
      const option = value as AnyMap;
      return option.enum ?? option.values ?? option.options ?? option.choices ?? option.default;
    }
    return undefined;
  };
  const merge = (source: unknown) => {
    if (!source || typeof source !== "object") return;
    if (Array.isArray(source)) {
      for (const entry of source) {
        if (!entry || typeof entry !== "object" || Array.isArray(entry)) continue;
        const option = entry as AnyMap;
        const key = String(option.name ?? option.key ?? option.id ?? option.parameter ?? "").trim();
        if (key) params[key] = optionValues(option) ?? params[key];
        merge(option.properties);
      }
      return;
    }
    const map = source as AnyMap;
    const properties = map.properties;
    if (properties && typeof properties === "object" && !Array.isArray(properties)) {
      for (const [key, definition] of Object.entries(properties as AnyMap)) {
        if (definition && typeof definition === "object") {
          const option = definition as AnyMap;
          params[key] = optionValues(option) ?? params[key];
        } else if (Array.isArray(definition)) params[key] = definition;
      }
    }
    for (const [key, value] of Object.entries(map)) {
      if (key !== "properties") {
        const values = optionValues(value);
        if (values !== undefined) params[key] = values;
      }
    }
    merge(map.input_schema);
    merge(map.parameters);
  };
  const scan = (source: unknown, depth = 0) => {
    if (!source || typeof source !== "object" || depth > 6) return;
    if (Array.isArray(source)) { for (const entry of source) scan(entry, depth + 1); return; }
    const map = source as AnyMap;
    for (const [rawKey, value] of Object.entries(map)) {
      const key = keyAliases[rawKey] ?? rawKey.toLowerCase();
      if (knownKeys.has(key)) {
        const values = optionValues(value);
        if (values !== undefined) params[key] = values;
      }
      scan(value, depth + 1);
    }
  };
  for (const key of ["params", "settings", "options", "parameters", "input_schema", "schema", "input", "config", "request_schema"]) {
    merge(item[key]);
    scan(item[key]);
  }
  scan(item);
  for (const key of ["quality", "aspect_ratio", "resolution", "size", "speed", "processing_speed", "count", "quantity"]) {
    if (item[key] !== undefined && params[key] === undefined) params[key] = item[key];
  }
  const identity = `${item.id ?? item.slug ?? item.model ?? ""} ${item.name ?? item.title ?? ""}`.toLowerCase();
  if (/gpt(?:[- ]?image)?[- ]?2/.test(identity)) {
    params.quality ??= ["low", "medium", "high"];
    params.resolution ??= ["1k", "2k", "4k"];
  }
  return params;
}

async function detailedModel(item: AnyMap) {
  const id = String(item.id ?? item.slug ?? item.model ?? "");
  if (!id) return item;
  try {
    const source = await provider(`/models/${encodeURIComponent(id)}`);
    const detail = source.model && typeof source.model === "object" ? source.model as AnyMap : source;
    return { ...item, ...detail, params: { ...modelParams(item), ...modelParams(detail) } };
  } catch {
    return item;
  }
}

async function models(admin: any) {
  if (modelCache && modelCache.expires > Date.now()) return modelCache.models;
  const source = await provider("/models");
  const rows = Array.isArray(source) ? source : Array.isArray(source.models) ? source.models : [];
  const allowedRows = rows.filter((row) => {
    const item = row as AnyMap;
    const type = String(item.type ?? item.category ?? "").toLowerCase();
    const identity = `${item.id ?? item.slug ?? item.model ?? ""} ${item.name ?? item.title ?? ""}`.toLowerCase();
    return (type === "image" || type.includes("image")) && isAllowedImageModel(identity);
  });
  const imageRows = await Promise.all(allowedRows.map(async (row) => {
    const item = await detailedModel(row as AnyMap);
    return {
      id: String(item.id ?? item.slug ?? item.model),
      name: item.name ?? item.title ?? item.id,
      type: "image",
      servers: item.servers ?? [],
      pricing: item.pricing ?? [],
      modes: item.modes ?? [],
      params: modelParams(item),
      notes: item.notes ?? null,
    };
  }));
  const { data, error: syncError } = await adminRpc(admin, "sync", { models: imageRows });
  if (syncError) throw new Error(syncError);
  const catalog = Array.isArray(data) ? data as AnyMap[] : [];
  const byId = new Map(catalog.map((item) => [String(item.id), item]));
  const merged = imageRows.map((model) => ({
    ...model,
    name: byId.get(String(model.id))?.name ?? model.name,
    creditCost: Number(byId.get(String(model.id))?.creditCost ?? 10),
    active: byId.get(String(model.id))?.active !== false,
    pricingVersion: byId.get(String(model.id))?.pricingVersion ?? 1,
  })).filter((model) => model.active && model.creditCost > 0);
  modelCache = { expires: Date.now() + MODEL_CACHE_MS, models: merged };
  return merged;
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

function pricingCost(model: AnyMap): number {
  const value = Number(model.creditCost ?? 0);
  return Number.isSafeInteger(value) && value > 0 ? value : 0;
}

async function currentModelPricing(admin: any, modelId: string, settings: AnyMap) {
  const { data, error: catalogError } = await adminRpc(admin, "quote", { modelId, settings });
  if (catalogError) throw new Error(catalogError);
  const row = data as AnyMap | null;
  return row ? { creditCost: pricingCost(row), pricingVersion: Number(row.pricingVersion ?? 1) } : null;
}

function requestHash(value: unknown) {
  return crypto.subtle.digest("SHA-256", new TextEncoder().encode(JSON.stringify(value)))
    .then((buffer) => Array.from(new Uint8Array(buffer)).map((x) => x.toString(16).padStart(2, "0")).join("").toUpperCase());
}

function readReferenceImages(value: unknown): string[] {
  if (value === undefined || value === null) return [];
  if (!Array.isArray(value) || value.length > 5) throw new Error("AI_REFERENCE_INVALID");
  return value.map((item) => {
    if (typeof item !== "string" || !/^data:image\/png;base64,[A-Za-z0-9+/]+={0,2}$/.test(item)) {
      throw new Error("AI_REFERENCE_INVALID");
    }
    if (item.length > 11_200_000) throw new Error("AI_REFERENCE_TOO_LARGE");
    return item;
  });
}

async function rpc(admin: any, action: string, payload: AnyMap) {
  const { data, error: rpcError } = await admin.rpc("ai_image_api", { action, payload });
  if (rpcError) throw new Error(rpcError.message || "AI_DATABASE_ERROR");
  return data as AnyMap;
}

async function adminRpc(admin: any, action: string, payload: AnyMap) {
  const { data, error: rpcError } = await admin.rpc("ai_model_catalog_api", { action, payload });
  return { data, error: rpcError?.message ?? null };
}

type VertexConfiguration = {
  credentialId: string;
  credentialsJson: string;
  projectId: string;
  region: string;
  modelId: string;
};

async function acquireVertexCredential(admin: any): Promise<VertexConfiguration> {
  const { data, error: rpcError } = await admin.rpc("ai_vertex_credential_acquire_api");
  if (rpcError || !data || typeof data !== "object") throw new Error("VERTEX_POOL_UNAVAILABLE");
  const value = data as AnyMap;
  if (typeof value.credentialId !== "string" || typeof value.credentialsJson !== "string"
    || typeof value.projectId !== "string" || typeof value.region !== "string" || typeof value.modelId !== "string") {
    throw new Error("VERTEX_POOL_UNAVAILABLE");
  }
  return value as VertexConfiguration;
}

async function reportVertexCredential(
  admin: any, credentialId: string, outcome: VertexCredentialOutcome, errorCode?: string,
) {
  const { error: rpcError } = await admin.rpc("ai_vertex_credential_report_api", {
    input_credential_id: credentialId,
    input_outcome: outcome,
    input_error_code: errorCode ?? null,
  });
  if (rpcError) throw new Error("VERTEX_POOL_REPORT_FAILED");
}

async function composeWithCredentialPool(
  admin: any, brief: string, referenceImages: string[],
) {
  let lastError: Error | null = null;
  for (let attempt = 0; attempt < 2; attempt += 1) {
    const configuration = await acquireVertexCredential(admin);
    try {
      const prompt = await composeWithVertex(configuration, brief, referenceImages);
      await reportVertexCredential(admin, configuration.credentialId, "success");
      return prompt;
    } catch (caught) {
      const typed = caught instanceof VertexCompositionError ? caught : null;
      const outcome: VertexCredentialOutcome = typed?.outcome ?? "transient";
      const code = typed?.message ?? "VERTEX_COMPOSITION_FAILED";
      await reportVertexCredential(admin, configuration.credentialId, outcome, code);
      lastError = new Error(code);
      if (outcome === "quota" || outcome === "transient" || outcome === "auth") continue;
    }
  }
  throw lastError ?? new Error("VERTEX_POOL_UNAVAILABLE");
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
    if (action === "models") return json({ models: await models(admin) });
    if (action === "history") return json(await rpc(admin, "history", { userId: userData.user.id }));
    if (action === "compose") {
      if (req.method !== "POST") return error("METHOD_NOT_ALLOWED", 405);
      const body = await req.json() as AnyMap;
      const brief = String(body.prompt ?? "").trim();
      if (!brief || brief.length > 16_000) return error("AI_PROMPT_INVALID", 400);
      return json({ prompt: await composeWithCredentialPool(admin, brief, readReferenceImages(body.reference_images)) });
    }
    if (action === "generate") {
      if (req.method !== "POST") return error("METHOD_NOT_ALLOWED", 405);
      const body = await req.json() as AnyMap;
      const modelId = String(body.model ?? "");
      const catalog = await models(admin);
      const model = modelById(catalog, modelId);
      if (!model) return error("AI_MODEL_NOT_ALLOWED", 400);
      const prompt = String(body.prompt ?? "").trim();
      if (!prompt || prompt.length > 16_000) return error("AI_PROMPT_INVALID", 400);
      const referenceImages = readReferenceImages(body.reference_images);
      const settings = pickSettings(model, (body.settings as AnyMap) ?? {});
      const idempotencyKey = String(body.idempotency_key ?? crypto.randomUUID());
      const request = { model: modelId, prompt, settings, referenceCount: referenceImages.length };
      const hash = await requestHash(request);
      const currentPricing = await currentModelPricing(admin, modelId, settings);
      const creditCost = currentPricing?.creditCost ?? 0;
      if (!creditCost) return error("AI_MODEL_PRICING_UNAVAILABLE", 409);
      const prepared = await rpc(admin, "prepare", {
        userId: userData.user.id, model: modelId, settings, requestHash: hash,
        idempotencyKey, creditCost, pricingVersion: `internal-${currentPricing?.pricingVersion ?? 1}`,
      });
      if (prepared.replayed && prepared.providerJobId) return json({ job_id: prepared.jobId });
      const submitted = await provider("/image/generate", {
        method: "POST",
        body: JSON.stringify({ model: modelId, prompt, ...settings,
          ...(referenceImages.length ? { reference_images: referenceImages } : {}) }),
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
      const remote = await provider(`/jobs/${encodeURIComponent(String(current.providerJobId))}`);
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
