import { createClient } from "@supabase/supabase-js";

type AnyMap = Record<string, unknown>;

const rateBuckets = new Map<string, { count: number; resetAt: number }>();
const RATE_WINDOW_MS = 5 * 60_000;
const RATE_LIMIT = 20;
const MAX_JSON_BYTES = 16 * 1024;

function allowedOrigin(req: Request) {
  const origin = req.headers.get("origin") ?? "";
  const configured = (Deno.env.get("PAYMENT_ALLOWED_ORIGINS") ?? "")
    .split(",").map((value) => value.trim()).filter(Boolean);
  return configured.includes(origin) ? origin : "";
}

function headers(req: Request) {
  const origin = allowedOrigin(req);
  return {
    ...(origin ? { "Access-Control-Allow-Origin": origin, "Vary": "Origin" } : {}),
    "Access-Control-Allow-Headers": "authorization, apikey, content-type, x-idempotency-key",
    "Access-Control-Allow-Methods": "GET,POST,DELETE,OPTIONS",
    "Content-Type": "application/json",
    "Cache-Control": "no-store",
  };
}

function json(req: Request, body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: headers(req) });
}

function fail(req: Request, code: string, status: number) {
  return json(req, { error: code }, status);
}

function bearer(req: Request) {
  const value = req.headers.get("authorization") ?? "";
  return value.startsWith("Bearer ") ? value.slice(7).trim() : "";
}

async function readJson(req: Request): Promise<AnyMap> {
  const declared = Number(req.headers.get("content-length") ?? "0");
  if (declared > MAX_JSON_BYTES) throw new Error("PAYMENT_PAYLOAD_TOO_LARGE");
  const raw = new Uint8Array(await req.arrayBuffer());
  if (!raw.length || raw.length > MAX_JSON_BYTES) throw new Error("PAYMENT_PAYLOAD_INVALID");
  const value = JSON.parse(new TextDecoder().decode(raw));
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error("PAYMENT_PAYLOAD_INVALID");
  }
  return value as AnyMap;
}

async function hash(value: string) {
  const bytes = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return Array.from(new Uint8Array(bytes)).map((x) => x.toString(16).padStart(2, "0")).join("");
}

async function rateLimited(req: Request) {
  const raw = (req.headers.get("x-forwarded-for") ?? "unknown").split(",")[0].trim();
  const key = await hash(`payments:${raw}`);
  const now = Date.now();
  const current = rateBuckets.get(key);
  if (!current || current.resetAt <= now) {
    if (rateBuckets.size > 1000) {
      for (const [entry, value] of rateBuckets) if (value.resetAt <= now) rateBuckets.delete(entry);
    }
    rateBuckets.set(key, { count: 1, resetAt: now + RATE_WINDOW_MS });
    return false;
  }
  current.count += 1;
  return current.count > RATE_LIMIT;
}

function merchantConfig() {
  const accountNumber = (Deno.env.get("PAYMENT_BANK_ACCOUNT") ?? "").trim();
  const bankCode = (Deno.env.get("PAYMENT_BANK_CODE") ?? "").trim();
  const accountHolder = (Deno.env.get("PAYMENT_ACCOUNT_HOLDER") ?? "").trim();
  const ttlMinutes = Number(Deno.env.get("PAYMENT_ORDER_TTL_MINUTES") ?? "");
  if (!/^[A-Za-z0-9]{4,19}$/.test(accountNumber) || !/^[A-Za-z0-9]{2,20}$/.test(bankCode)
    || !accountHolder || accountHolder.length > 80 || !Number.isInteger(ttlMinutes)
    || ttlMinutes < 5 || ttlMinutes > 1440) throw new Error("PAYMENT_MERCHANT_NOT_CONFIGURED");
  return { accountNumber, bankCode, accountHolder, ttlMinutes };
}

function withPaymentInstructions(order: AnyMap, merchant: ReturnType<typeof merchantConfig>) {
  const params = new URLSearchParams({
    acc: merchant.accountNumber,
    bank: merchant.bankCode,
    amount: String(order.priceVnd),
    des: String(order.paymentContent),
    template: "compact",
  });
  return { ...order, bankCode: merchant.bankCode, accountNumber: merchant.accountNumber,
    accountHolder: merchant.accountHolder, qrUrl: `https://vietqr.app/img?${params}` };
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") {
    return allowedOrigin(req) ? new Response(null, { status: 204, headers: headers(req) })
      : fail(req, "ORIGIN_NOT_ALLOWED", 403);
  }
  const supabaseUrl = Deno.env.get("SUPABASE_URL");
  const anonKey = Deno.env.get("SUPABASE_ANON_KEY");
  const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  if (!supabaseUrl || !anonKey || !serviceKey) return fail(req, "PAYMENT_BACKEND_UNAVAILABLE", 503);
  if (req.headers.get("origin") && !allowedOrigin(req)) return fail(req, "ORIGIN_NOT_ALLOWED", 403);
  if (await rateLimited(req)) return fail(req, "PAYMENT_RATE_LIMITED", 429);

  const admin = createClient(supabaseUrl, serviceKey, { auth: { persistSession: false } });
  const url = new URL(req.url);
  const segments = url.pathname.split("/").filter(Boolean);
  const afterFunction = segments.slice(segments.lastIndexOf("payments") + 1);

  // Catalog is public, skip auth validation
  if (req.method === "GET" && afterFunction[0] === "catalog") {
    const { data, error } = await admin.rpc("payment_api", { action: "catalog", payload: {} });
    if (error) throw new Error(error.message || "PAYMENT_DATABASE_ERROR");
    return json(req, { products: data }, 200);
  }

  const token = bearer(req);
  let userId = "";
  let recentlyAuthenticated = false;
  if (token) {
    const userClient = createClient(supabaseUrl, anonKey, {
      global: { headers: { Authorization: `Bearer ${token}` } }, auth: { persistSession: false },
    });
    const { data, error } = await userClient.auth.getUser(token);
    if (error || !data.user) return fail(req, "PAYMENT_AUTH_REQUIRED", 401);
    userId = data.user.id;
    const lastSignInAt = Date.parse(data.user.last_sign_in_at ?? "");
    recentlyAuthenticated = Number.isFinite(lastSignInAt)
      && Date.now() - lastSignInAt <= 15 * 60_000;
  }
  const rpc = async (action: string, payload: AnyMap) => {
    const { data, error } = await admin.rpc("payment_api", { action, payload });
    if (error) throw new Error(error.message || "PAYMENT_DATABASE_ERROR");
    return data as AnyMap;
  };

  try {
    if (req.method === "POST" && afterFunction[0] === "orders" && afterFunction.length === 1) {
      const body = await readJson(req);
      const deviceCode = String(body.device_code ?? "").toUpperCase();
      if (!userId && !/^AMS-[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{4}$/.test(deviceCode)) {
        return fail(req, "PAYMENT_DEVICE_UNAVAILABLE", 400);
      }
      const productId = String(body.product_id ?? "");
      const idempotencyKey = req.headers.get("x-idempotency-key") ?? String(body.idempotency_key ?? "");
      if (!/^[A-Za-z0-9_.:-]{1,64}$/.test(productId) || !/^[A-Za-z0-9_.:-]{8,128}$/.test(idempotencyKey)) {
        return fail(req, "PAYMENT_ORDER_REQUEST_INVALID", 400);
      }
      const merchant = merchantConfig();
      const order = await rpc("create_order", { userId, deviceCode, productId,
        idempotencyKey, ttlMinutes: merchant.ttlMinutes });
      return json(req, withPaymentInstructions(order, merchant), 201);
    }
    if (afterFunction[0] === "orders" && afterFunction.length === 2
      && ["GET", "DELETE"].includes(req.method)) {
      if (!/^[0-9a-f-]{36}$/i.test(afterFunction[1])) return fail(req, "PAYMENT_ORDER_NOT_FOUND", 404);
      const deviceCode = String(url.searchParams.get("device_code") ?? "").toUpperCase();
      const action = req.method === "DELETE" ? "cancel_order" : "order_status";
      const order = await rpc(action, { userId, deviceCode, orderId: afterFunction[1] });
      return json(req, order);
    }
    if (afterFunction[0] === "admin" && userId) {
      if (req.method === "GET" && afterFunction[1] === "metrics") {
        return json(req, await rpc("admin_metrics", { adminUserId: userId }));
      }
      if (afterFunction[1] === "products") {
        if (req.method === "GET") return json(req, { products: await rpc("admin_products", { adminUserId: userId }) });
        if (req.method === "POST") {
          const body = await readJson(req);
          return json(req, await rpc("admin_product_save", { ...body, adminUserId: userId,
            recentAuth: recentlyAuthenticated }));
        }
      }
      if (req.method === "GET" && afterFunction[1] === "orders" && afterFunction.length === 2) {
        const orders = await rpc("admin_orders", { adminUserId: userId,
          status: url.searchParams.get("status") ?? "", limit: 100 });
        return json(req, { orders });
      }
      if (req.method === "GET" && afterFunction[1] === "orders" && afterFunction.length === 3) {
        return json(req, await rpc("admin_order_detail", { adminUserId: userId, orderId: afterFunction[2] }));
      }
      if (req.method === "POST" && afterFunction[1] === "orders" && afterFunction[3] === "retry") {
        const body = await readJson(req);
        return json(req, await rpc("admin_retry", { adminUserId: userId, orderId: afterFunction[2],
          reason: String(body.reason ?? ""), correlationId: String(body.correlation_id ?? ""),
          recentAuth: recentlyAuthenticated }));
      }
    }
    return fail(req, "PAYMENT_ROUTE_NOT_FOUND", 404);
  } catch (caught) {
    const message = caught instanceof Error ? caught.message : "PAYMENT_REQUEST_FAILED";
    const known = message.match(/PAYMENT_[A-Z_]+|ADMIN_[A-Z_]+/)?.[0];
    return fail(req, known ?? "PAYMENT_REQUEST_FAILED",
      known === "PAYMENT_ORDER_NOT_FOUND" ? 404 : known?.startsWith("ADMIN_") ? 403 : 400);
  }
});
