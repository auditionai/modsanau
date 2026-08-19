import { createClient } from "@supabase/supabase-js";
import { composeWithVertex, VertexCompositionError, type VertexCredentialOutcome } from "../_shared/vertex-ai.ts";

const cors = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "GET,POST,OPTIONS",
  "Content-Type": "application/json",
};

const GPTI2_BASE = "https://gpti2.store/v1";
const MODEL_CACHE_MS = 5 * 60_000;
let modelCache: { expires: number; models: unknown[] } | null = null;
const GPTI2_SIZES = ["1024x1024", "1536x1536", "2048x2048", "1280x720", "2560x1440", "3840x2160", "720x1280", "1440x2560", "2160x3840", "1024x768", "2048x1536", "3200x2400", "768x1024", "1536x2048", "2400x3200", "1536x1024", "2400x1600", "3360x2240", "1024x1536", "1600x2400", "2240x3360", "1280x544", "2560x1088", "3840x1632"];

function gpti2Pricing() {
  return GPTI2_SIZES.flatMap((size) => ["low", "medium", "high"].flatMap((quality) => [1, 2, 3, 4].map((n) => ({ size, quality, n, cost: 50 * n }))));
}

type AnyMap = Record<string, unknown>;

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
  const key = Deno.env.get("GPTI2_API_KEY");
  if (!key) throw new Error("GPTI2_API_KEY_NOT_CONFIGURED");
  const headers = new Headers(init.headers);
  headers.set("Authorization", `Bearer ${key}`);
  headers.set("Accept", "application/json");
  if (init.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  const response = await fetch(`${GPTI2_BASE}${path}`, { ...init, headers });
  const text = await response.text();
  let body: unknown = {};
  try { body = text ? JSON.parse(text) : {}; } catch { body = {}; }
  if (!response.ok) {
    const code = (body as AnyMap)?.error ?? (body as AnyMap)?.message;
    throw new Error(typeof code === "string" ? code : `GPTI2_HTTP_${response.status}`);
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
  const normalized = normalizeModelId(identity);
  if (normalized === "gpt-image-2") {
    params.size = ["1024x1024", "1536x1536", "2048x2048", "1280x720", "2560x1440", "3840x2160", "720x1280", "1440x2560", "2160x3840", "1024x768", "2048x1536", "3200x2400", "768x1024", "1536x2048", "2400x3200", "1536x1024", "2400x1600", "3360x2240", "1024x1536", "1600x2400", "2240x3360", "1280x544", "2560x1088", "3840x1632"];
    params.quality = ["low", "medium", "high"];
    params.n = ["1", "2", "3", "4"];
  } else if (normalized === "nano-banana-pro") {
    params.size ??= ["1024x1024", "1536x1536", "2048x2048", "1280x720", "2560x1440", "3840x2160", "720x1280", "1440x2560", "2160x3840", "1024x768", "2048x1536", "3200x2400", "768x1024", "1536x2048", "2400x3200", "1536x1024", "2400x1600", "3360x2240", "1024x1536", "1600x2400", "2240x3360", "1280x544", "2560x1088", "3840x1632"];
    params.quality ??= ["low", "medium", "high"];
    params.n ??= ["1", "2", "3", "4"];
  }
  return params;
}

function pricingParams(item: AnyMap): AnyMap {
  const result: AnyMap = {};
  const pricing = Array.isArray(item.pricing) ? item.pricing : [];
  const add = (key: string, value: unknown) => {
    if (value === undefined || value === null || value === "") return;
    const values = result[key] instanceof Set ? result[key] as Set<string> : new Set<string>();
    values.add(String(value).toLowerCase());
    result[key] = values;
  };
  for (const row of pricing) {
    if (!row || typeof row !== "object" || Array.isArray(row)) continue;
    const entry = row as AnyMap;
    add("server", entry.server);
    const rawResolution = entry.resolution ?? entry.size;
    const normalizedResolution = String(rawResolution ?? "").toLowerCase();
    if (["low", "medium", "high"].includes(normalizedResolution)) add("quality", normalizedResolution);
    else if (rawResolution !== undefined) add("resolution", rawResolution);
    for (const key of ["quality", "aspect_ratio", "speed", "processing_speed", "count", "quantity"]) add(key, entry[key]);
    const config = String(entry.config_key ?? entry.key ?? "").toLowerCase();
    for (const value of ["1k", "2k", "4k"]) if (config.includes(value)) add("resolution", value);
    for (const value of ["low", "medium", "high"]) if (config.includes(value)) add("quality", value);
    for (const value of ["fast", "slow"]) if (config.includes(value)) add("speed", value);
  }
  return Object.fromEntries(Object.entries(result).map(([key, value]) => [key, [...(value as Set<string>)]]));
}

async function detailedModel(item: AnyMap) {
  return { ...item, params: modelParams(item) };
}

async function models(admin: any) {
  if (modelCache && modelCache.expires > Date.now()) return modelCache.models;
  const imageRows = [
    { id: "gpt-image-2", name: "GPT Image 2", type: "image", servers: [], pricing: gpti2Pricing(), modes: [], params: { size: GPTI2_SIZES, quality: ["low", "medium", "high"] }, notes: null },
    { id: "nano-banana-pro", name: "Nano Banana PRO", type: "image", servers: [], pricing: gpti2Pricing(), modes: [], params: { size: GPTI2_SIZES, quality: ["low", "medium", "high"] }, notes: null },
  ];
  const { data, error: syncError } = await adminRpc(admin, "sync", { models: imageRows });
  if (syncError) throw new Error(syncError);
  const catalog = Array.isArray(data) ? data as AnyMap[] : [];
  const byId = new Map(catalog.map((item) => [String(item.id), item]));
  const deduped = [...new Map(imageRows.map((model) => [model.id, model])).values()];
  const merged = deduped.map((model) => ({
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

function fallbackPricing(model: AnyMap, settings: AnyMap) {
  const rows = Array.isArray(model.pricing) ? model.pricing.filter((row): row is AnyMap => !!row && typeof row === "object" && !Array.isArray(row)) : [];
  const matches = rows.filter((row) => Object.entries(settings).every(([key, value]) => {
    const expected = String(value).toLowerCase();
    const actual = String(row[key] ?? "").toLowerCase();
    const config = String(row.config_key ?? row.key ?? "").toLowerCase();
    return actual === expected || config.includes(expected);
  }));
  const row = matches.sort((a, b) => Number(a.internalCredits ?? a.creditCost ?? 0) - Number(b.internalCredits ?? b.creditCost ?? 0))[0];
  const cost = Number(row?.internalCredits ?? row?.creditCost ?? 0);
  return Number.isSafeInteger(cost) && cost > 0 ? { creditCost: cost, pricingVersion: 1 } : null;
}

async function currentModelPricing(admin: any, modelId: string, settings: AnyMap) {
  const { data, error: quoteError } = await admin.rpc("gpti2_internal_quote", {
    model_id_value: modelId,
    requested_settings: settings,
  });
  if (quoteError) throw new Error(quoteError.message || "AI_MODEL_PRICING_UNAVAILABLE");
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

function readCreativeInputs(value: unknown) {
  const source = value && typeof value === "object" && !Array.isArray(value) ? value as AnyMap : {};
  const read = (key: string) => String(source[key] ?? "").trim().slice(0, 1000);
  return { theme: read("theme"), style: read("style"), composition: read("composition"), palette: read("palette"), note: read("note") };
}

function readOutput(value: unknown) {
  const source = value && typeof value === "object" && !Array.isArray(value) ? value as AnyMap : {};
  const width = Number(source.width), height = Number(source.height);
  if (!Number.isInteger(width) || !Number.isInteger(height) || width < 64 || height < 64 || width > 8192 || height > 8192 || width * height > 32_000_000) return null;
  return { width, height };
}

async function activePreset(admin: any, presetId: string) {
  if (!/^[0-9a-f-]{36}$/i.test(presetId)) throw new Error("AI_PRESET_REQUIRED");
  const { data, error: presetError } = await admin.from("ai_image_prompt_presets")
    .select("preset_id, display_name, base_prompt").eq("preset_id", presetId).eq("is_active", true).is("deleted_at", null).maybeSingle();
  if (presetError || !data) throw new Error("AI_PRESET_UNAVAILABLE");
  return data as { preset_id: string; display_name: string; base_prompt: string };
}

async function pngGuideCanvas(sourceSize: string, output: { width: number; height: number }) {
  const match = /^(\d{3,4})x(\d{3,4})$/.exec(sourceSize);
  if (!match) return null;
  const width = Number(match[1]), height = Number(match[2]);
  const targetRatio = output.width / output.height, sourceRatio = width / height;
  if (!Number.isFinite(targetRatio) || Math.abs(targetRatio - sourceRatio) < 0.002) return null;
  const safeWidth = targetRatio >= sourceRatio ? width : Math.max(1, Math.round(height * targetRatio));
  const safeHeight = targetRatio >= sourceRatio ? Math.max(1, Math.round(width / targetRatio)) : height;
  const left = Math.floor((width - safeWidth) / 2), top = Math.floor((height - safeHeight) / 2);
  const raw = new Uint8Array((width * 3 + 1) * height);
  for (let y = 0; y < height; y += 1) {
    const offset = y * (width * 3 + 1); raw[offset] = 0;
    for (let x = 0; x < width; x += 1) {
      const inside = x >= left && x < left + safeWidth && y >= top && y < top + safeHeight;
      const color = inside ? 255 : 0; const pixel = offset + 1 + x * 3;
      raw[pixel] = color; raw[pixel + 1] = color; raw[pixel + 2] = color;
    }
  }
  const crc = (bytes: Uint8Array) => { let value = 0xffffffff; for (const byte of bytes) { value ^= byte; for (let bit = 0; bit < 8; bit += 1) value = (value >>> 1) ^ ((value & 1) ? 0xedb88320 : 0); } return (value ^ 0xffffffff) >>> 0; };
  const chunk = (type: string, body: Uint8Array) => { const result = new Uint8Array(body.length + 12); const view = new DataView(result.buffer); view.setUint32(0, body.length); result.set(new TextEncoder().encode(type), 4); result.set(body, 8); view.setUint32(body.length + 8, crc(result.slice(4, body.length + 8))); return result; };
  const ihdr = new Uint8Array(13); const header = new DataView(ihdr.buffer); header.setUint32(0, width); header.setUint32(4, height); ihdr[8] = 8; ihdr[9] = 2;
  const compressed = new Uint8Array(await new Response(new Blob([raw]).stream().pipeThrough(new CompressionStream("deflate"))).arrayBuffer());
  const parts = [new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]), chunk("IHDR", ihdr), chunk("IDAT", compressed), chunk("IEND", new Uint8Array())];
  const length = parts.reduce((total, part) => total + part.length, 0); const png = new Uint8Array(length); let offset = 0;
  for (const part of parts) { png.set(part, offset); offset += part.length; }
  let binary = ""; for (const byte of png) binary += String.fromCharCode(byte);
  return { image: `data:image/png;base64,${btoa(binary)}`, safe: { left, top, width: safeWidth, height: safeHeight }, source: { width, height } };
}

function firstProviderResult(value: unknown): string | null {
  if (!value || typeof value !== "object") return null;
  const map = value as AnyMap;
  if (typeof map.result === "string") return map.result;
  if (typeof map.url === "string") return map.url;
  const data = Array.isArray(map.data) ? map.data : [];
  const first = data[0] && typeof data[0] === "object" ? data[0] as AnyMap : null;
  if (typeof first?.url === "string") return first.url;
  if (typeof first?.b64_json === "string") return `data:image/png;base64,${first.b64_json}`;
  return null;
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
    if (action === "presets") {
      const { data, error: presetError } = await admin.rpc("ai_image_prompt_preset_api", { action: "active_list", payload: {} });
      if (presetError) throw new Error("AI_PRESET_UNAVAILABLE");
      return json({ presets: Array.isArray(data) ? data : [] });
    }
    if (action === "history") return json(await rpc(admin, "history", { userId: userData.user.id }));
    if (action === "cancel") {
      if (req.method !== "POST") return error("METHOD_NOT_ALLOWED", 405);
      const body = await req.json() as AnyMap;
      const jobId = String(body.job_id ?? body.jobId ?? "");
      if (!/^[0-9a-f-]{36}$/i.test(jobId)) return error("AI_JOB_INVALID", 400);
      const { data, error: cancelError } = await admin.rpc("ai_job_cancel_api", {
        p_user_id: userData.user.id, p_job_id: jobId, p_finalize: Boolean(body.finalize),
      });
      if (cancelError) throw new Error(cancelError.message || "AI_JOB_CANCEL_FAILED");
      return json(data);
    }
    if (action === "quote") {
      if (req.method !== "POST") return error("METHOD_NOT_ALLOWED", 405);
      const body = await req.json() as AnyMap;
      const modelId = String(body.model ?? "");
      const catalog = await models(admin);
      const model = modelById(catalog, modelId);
      if (!model) return error("AI_MODEL_NOT_ALLOWED", 400);
      const settings = pickSettings(model, (body.settings as AnyMap) ?? {});
      let currentPricing: { creditCost: number; pricingVersion: number } | null = null;
      try { currentPricing = await currentModelPricing(admin, modelId, settings); } catch { currentPricing = null; }
      currentPricing ??= null;
      if (!currentPricing?.creditCost) return error("AI_MODEL_PRICING_UNAVAILABLE", 409);
      return json({ creditCost: currentPricing.creditCost, pricingVersion: `internal-${currentPricing.pricingVersion ?? 1}`, settings });
    }
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
      const { data: hasActive, error: activeError } = await admin.rpc("ai_user_active_job_api", { p_user_id: userData.user.id });
      if (activeError) throw new Error(activeError.message || "AI_JOB_ACTIVE_CHECK_FAILED");
      if (hasActive === true) return error("AI_JOB_ALREADY_ACTIVE", 409);
      const preset = await activePreset(admin, String(body.preset_id ?? body.presetId ?? ""));
      const referenceImages = readReferenceImages(body.reference_images);
      const creative = readCreativeInputs(body.creative_inputs);
      const settings = pickSettings(model, (body.settings as AnyMap) ?? {});
      const output = readOutput(body.output);
      let guide: Awaited<ReturnType<typeof pngGuideCanvas>> | null = null;
      if (output && typeof settings.size === "string") {
        if (referenceImages.length >= 5) return error("AI_REFERENCE_LIMIT_EXCEEDED", 400);
        guide = await pngGuideCanvas(settings.size, output);
        if (guide) referenceImages.push(guide.image);
      }
      const brief = [
        `Base prompt: ${preset.base_prompt}`,
        creative.theme && `Theme: ${creative.theme}`,
        creative.style && `Visual style: ${creative.style}`,
        creative.composition && `Composition: ${creative.composition}`,
        creative.palette && `Color palette: ${creative.palette}`,
        creative.note && `Additional user note: ${creative.note}`,
        guide && `The final texture target is ${output!.width}x${output!.height}. The last supplied image is a layout guide: black is forbidden margin; design only inside the white safe area. Keep all essential content inside that white area.`,
      ].filter(Boolean).join("\n");
      const prompt = await composeWithCredentialPool(admin, brief, referenceImages);
      const idempotencyKey = String(body.idempotency_key ?? crypto.randomUUID());
      const request = { model: modelId, presetId: preset.preset_id, creative, settings, output, referenceCount: referenceImages.length };
      const hash = await requestHash(request);
      const currentPricing = await currentModelPricing(admin, modelId, settings);
      const creditCost = currentPricing?.creditCost ?? 0;
      if (!creditCost) return error("AI_MODEL_PRICING_UNAVAILABLE", 409);
      settings.n = 1;
      const prepared = await rpc(admin, "prepare", {
        userId: userData.user.id, model: modelId, settings: { ...settings, presetId: preset.preset_id, output, guide: guide?.safe ?? null }, requestHash: hash,
        idempotencyKey, creditCost, pricingVersion: `internal-${currentPricing?.pricingVersion ?? 1}`,
      });
      if (prepared.replayed && prepared.providerJobId) return json({ job_id: prepared.jobId });
      try {
        const providerPath = modelId === "nano-banana-pro" ? "/images/nano/generations" : "/images/jobs";
        const submitted = await provider(providerPath, {
          method: "POST",
          headers: { "Idempotency-Key": idempotencyKey, "Prefer": "respond-async" },
          body: JSON.stringify({ model: modelId, prompt, n: 1, ...settings,
            ...(referenceImages.length ? { reference_images: referenceImages } : {}) }),
        });
        const rawProviderJobId = String(submitted.job_id ?? submitted.id ?? "");
        const providerJobId = rawProviderJobId && modelId === "nano-banana-pro" ? `nano:${rawProviderJobId}` : rawProviderJobId;
        const directResult = firstProviderResult(submitted);
        if (!providerJobId && !directResult) throw new Error("GPTI2_JOB_ID_MISSING");
        await rpc(admin, "submitted", {
          userId: userData.user.id, jobId: prepared.jobId, providerJobId: providerJobId || `direct-${prepared.jobId}`,
        });
        if (directResult) {
          await rpc(admin, "complete", { userId: userData.user.id, jobId: prepared.jobId, resultUrl: directResult });
          return json({ job_id: prepared.jobId, status: "completed", result: directResult });
        }
        return json({ job_id: prepared.jobId, status: "queued" });
      } catch (caught) {
        try { await rpc(admin, "fail", { userId: userData.user.id, jobId: prepared.jobId, errorCode: "GPTI2_PROVIDER_FAILED" }); }
        catch { /* Preserve the original provider failure; reconciliation can inspect the durable job. */ }
        throw caught;
      }
    }
    if (action === "status") {
      const jobId = url.searchParams.get("job_id") ?? "";
      if (!/^[0-9a-f-]{36}$/i.test(jobId)) return error("AI_JOB_INVALID", 400);
      const current = await rpc(admin, "status", { userId: userData.user.id, jobId });
      if (current.status === "Completed" || current.status === "Failed") return json({
        job_id: jobId, status: String(current.status).toLowerCase(), result: current.resultUrl ?? null,
      });
      if (!current.providerJobId) return json({ job_id: jobId, status: "queued" });
      const storedProviderJobId = String(current.providerJobId);
      const isNanoJob = storedProviderJobId.startsWith("nano:");
      const remoteId = isNanoJob ? storedProviderJobId.slice(5) : storedProviderJobId;
      const remote = await provider(isNanoJob
        ? `/images/nano/${encodeURIComponent(remoteId)}`
        : `/images/jobs/${encodeURIComponent(remoteId)}`);
      const state = String(remote.status ?? remote.state ?? "processing").toLowerCase();
      const result = firstProviderResult(remote) ?? (typeof remote.output === "string" ? remote.output : null);
      if (result && /^https:\/\/[^\s]+$/.test(result)) {
        const completed = await rpc(admin, "complete", { userId: userData.user.id, jobId, resultUrl: result });
        return json({ job_id: jobId, status: "completed", result: completed.resultUrl ?? result });
      }
      if (["failed", "error", "cancelled"].includes(state)) {
        if (state === "cancelled") {
          const { error: cancelError } = await admin.rpc("ai_job_cancel_api", {
            p_user_id: userData.user.id, p_job_id: jobId, p_finalize: true,
          });
          if (cancelError) throw new Error(cancelError.message || "AI_JOB_CANCEL_FAILED");
        } else {
          await rpc(admin, "fail", { userId: userData.user.id, jobId, errorCode: "GPTI2_PROVIDER_FAILED" });
        }
        return json({ job_id: jobId, status: state });
      }
      return json({ job_id: jobId, status: "processing" });
    }
    return error("AI_ACTION_UNKNOWN", 400);
  } catch (caught) {
    const message = caught instanceof Error ? caught.message : "AI_PROVIDER_FAILED";
    const code = message.includes("ai_jobs_one_active_per_user_idx") ? "AI_JOB_ALREADY_ACTIVE"
      : message.startsWith("GPTI2_") || message.startsWith("AI_") ? message : "AI_PROVIDER_FAILED";
    return error(code, code === "AI_JOB_ALREADY_ACTIVE" ? 409 : 502);
  }
});
