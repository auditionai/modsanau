import { createClient } from "@supabase/supabase-js";

type AnyMap = Record<string, unknown>;

// Inline utilities
const MAX_BODY_BYTES = 1_048_576;

function hex(buffer: ArrayBuffer): string {
  return Array.from(new Uint8Array(buffer))
    .map(b => b.toString(16).padStart(2, '0'))
    .join('');
}

function normalizeSePayPayload(raw: AnyMap) {
  return {
    id: String(raw.id || ''),
    gateway: String(raw.gateway || ''),
    transaction_date: String(raw.transaction_date || ''),
    account_number: String(raw.account_number || ''),
    sub_account: String(raw.sub_account || ''),
    amount_in: String(raw.amount_in || '0'),
    amount_out: String(raw.amount_out || '0'),
    accumulated: String(raw.accumulated || '0'),
    code: String(raw.code || ''),
    transaction_content: String(raw.transaction_content || ''),
    reference_number: String(raw.reference_number || ''),
    body: String(raw.body || '')
  };
}

async function verifyHmac(payload: Uint8Array, timestamp: string, signature: string, secret: string): Promise<boolean> {
  const encoder = new TextEncoder();
  const key = await crypto.subtle.importKey(
    'raw',
    encoder.encode(secret),
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign']
  );
  const data = encoder.encode(timestamp + new TextDecoder().decode(payload));
  const mac = await crypto.subtle.sign('HMAC', key, data);
  const computed = hex(mac);
  return computed === signature;
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: {
    "Content-Type": "application/json", "Cache-Control": "no-store",
  } });
}

Deno.serve(async (req) => {
  if (req.method !== "POST") return json({ success: false, error: "METHOD_NOT_ALLOWED" }, 405);
  if (!(req.headers.get("content-type") ?? "").toLowerCase().startsWith("application/json")) {
    return json({ success: false, error: "CONTENT_TYPE_INVALID" }, 400);
  }
  const declared = Number(req.headers.get("content-length") ?? "0");
  if (declared > MAX_BODY_BYTES) return json({ success: false, error: "PAYLOAD_TOO_LARGE" }, 413);
  const raw = new Uint8Array(await req.arrayBuffer());
  if (!raw.length || raw.length > MAX_BODY_BYTES) return json({ success: false, error: "PAYLOAD_INVALID" }, 400);

  const secret = Deno.env.get("SEPAY_WEBHOOK_SECRET") ?? "";
  if (secret.length < 32) return json({ success: false, error: "WEBHOOK_NOT_CONFIGURED" }, 503);
  const timestamp = req.headers.get("x-sepay-timestamp") ?? "";
  const signature = req.headers.get("x-sepay-signature") ?? "";
  if (!await verifyHmac(raw, timestamp, signature, secret)) {
    return json({ success: false, error: "WEBHOOK_AUTH_INVALID" }, 401);
  }

  let body: AnyMap;
  try { body = JSON.parse(new TextDecoder().decode(raw)) as AnyMap; }
  catch { return json({ success: false, error: "WEBHOOK_PAYLOAD_INVALID" }, 400); }
  const normalized = normalizeSePayPayload(body);
  if (!normalized) return json({ success: false, error: "WEBHOOK_PAYLOAD_INVALID" }, 400);

  const supabaseUrl = Deno.env.get("SUPABASE_URL");
  const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  const expectedAccountNumber = (Deno.env.get("PAYMENT_BANK_ACCOUNT") ?? "").trim();
  if (!supabaseUrl || !serviceKey || !expectedAccountNumber) {
    return json({ success: false, error: "WEBHOOK_NOT_CONFIGURED" }, 503);
  }
  const payloadHash = hex(new Uint8Array(await crypto.subtle.digest("SHA-256", raw))).toUpperCase();
  const admin = createClient(supabaseUrl, serviceKey, { auth: { persistSession: false } });
  const { error } = await admin.rpc("payment_api", { action: "ingest_sepay", payload: {
    ...normalized, expectedAccountNumber, payloadSha256: payloadHash,
  } });
  if (error) return json({ success: false, error: "WEBHOOK_PERSISTENCE_FAILED" }, 500);
  return json({ success: true }, 200);
});
