import assert from "node:assert/strict";
import { randomBytes, webcrypto } from "node:crypto";
import { readFile } from "node:fs/promises";
import test from "node:test";
import {
  MAX_BODY_BYTES,
  normalizeSePayPayload,
  verifyHmac,
} from "../supabase/functions/_shared/sepay-webhook.mjs";

const secret = "plan-107-deterministic-webhook-secret";
const timestamp = "1786752000";
const nowSeconds = Number(timestamp);
const raw = new TextEncoder().encode(JSON.stringify({ id: 107, transferAmount: 50000 }));

async function signature(body = raw, signedAt = timestamp) {
  const key = await webcrypto.subtle.importKey("raw", new TextEncoder().encode(secret),
    { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const signed = new TextEncoder().encode(`${signedAt}.${new TextDecoder().decode(body)}`);
  const digest = new Uint8Array(await webcrypto.subtle.sign("HMAC", key, signed));
  return `sha256=${Array.from(digest, value => value.toString(16).padStart(2, "0")).join("")}`;
}

function fixture(overrides = {}) {
  return {
    id: 314159,
    gateway: "VCB",
    transactionDate: "2026-08-15 12:00:00",
    accountNumber: "1234567890",
    code: "AMS0123456789ABCDEF",
    content: "AMS0123456789ABCDEF",
    transferType: "in",
    transferAmount: 50000,
    referenceCode: "FT107",
    ...overrides,
  };
}

test("HMAC xác minh raw body, từ chối chữ ký sai và timestamp stale", async () => {
  const valid = await signature();
  assert.equal(await verifyHmac(raw, timestamp, valid, secret, nowSeconds), true);
  assert.equal(await verifyHmac(raw, timestamp, valid.replace(/.$/, "0"), secret, nowSeconds), false);
  assert.equal(await verifyHmac(raw, timestamp, valid, secret, nowSeconds + 301), false);
  assert.equal(await verifyHmac(new Uint8Array(MAX_BODY_BYTES + 1), timestamp, valid, secret, nowSeconds), false);
});

test("payload SePay chuẩn hóa incoming/outgoing và trích order code có giới hạn", () => {
  assert.deepEqual(normalizeSePayPayload(fixture()), {
    providerTransactionId: "314159",
    referenceCode: "FT107",
    gateway: "VCB",
    transactionDate: "2026-08-15T05:00:00.000Z",
    transferType: "in",
    amountVnd: 50000,
    paymentCode: "AMS0123456789ABCDEF",
    content: "AMS0123456789ABCDEF",
    accountNumber: "1234567890",
  });
  assert.equal(normalizeSePayPayload(fixture({ code: null, content: "nap AMSFEDCBA9876543210 ngay" }))?.paymentCode,
    "AMSFEDCBA9876543210");
  assert.equal(normalizeSePayPayload(fixture({ transferType: "out" }))?.transferType, "out");
  assert.equal(normalizeSePayPayload(fixture({ transferAmount: 1.5 })), null);
  assert.equal(normalizeSePayPayload(fixture({ accountNumber: "<script>" })), null);
  assert.equal(normalizeSePayPayload([]), null);
});

test("order code có format ngân hàng ổn định và không collision trong mẫu lớn", () => {
  const codes = new Set();
  for (let index = 0; index < 50000; index += 1) {
    const code = `AMS${randomBytes(10).toString("hex").slice(0, 16).toUpperCase()}`;
    assert.match(code, /^AMS[A-Z0-9]{16}$/);
    assert.equal(codes.has(code), false);
    codes.add(code);
  }
});

test("migration khóa các invariant tài chính và recovery", async () => {
  const sql = await readFile(new URL("../supabase/migrations/202608150005_plan107_sepay_payments.sql", import.meta.url), "utf8");
  for (const contract of [
    "provider_transaction_id varchar(128) UNIQUE",
    "UNIQUE (device_profile_id, idempotency_key)",
    "payment_order_id uuid NOT NULL UNIQUE",
    "matched_order_id uuid UNIQUE",
    "payment_orders_snapshot_immutable",
    "greatest(private.subscriptions.expires_at, clock_timestamp())",
    "private.credit_grant",
    "pg_advisory_xact_lock(hashtextextended('sepay:' || provider_id, 0))",
    "candidateOrderId",
    "recentAuth",
  ]) assert.ok(sql.includes(contract), `Thiếu contract: ${contract}`);
  assert.equal(/UPDATE\s+private\.credit_wallets/i.test(sql), false);
});

test("frontend chỉ nhận QR VietQR HTTPS, có backoff và CSP tương ứng", async () => {
  const [script, headers, desktopService, accountViewModel, paymentFunction] = await Promise.all([
    readFile(new URL("../web/script.js", import.meta.url), "utf8"),
    readFile(new URL("../web/_headers", import.meta.url), "utf8"),
    readFile(new URL("../src/AuditionModStudio.Cloud/SupabasePaymentService.cs", import.meta.url), "utf8"),
    readFile(new URL("../src/AuditionModStudio.App/Account/AccountViewModel.cs", import.meta.url), "utf8"),
    readFile(new URL("../supabase/functions/payments/index.ts", import.meta.url), "utf8"),
  ]);
  assert.match(script, /qrUrl\.hostname !== "vietqr\.app"/);
  assert.match(script, /\[4000, 5000, 7000, 10000, 15000, 20000, 30000\]/);
  assert.match(headers, /img-src[^\n]+https:\/\/vietqr\.app/);
  assert.match(desktopService, /qrUri\.Host, "vietqr\.app"/);
  assert.match(accountViewModel, /ExpiresAt[\s\S]+AddMinutes\(2\)/);
  assert.match(paymentFunction, /MAX_JSON_BYTES = 16 \* 1024/);
  assert.doesNotMatch(script, /SEPAY_(?:API_TOKEN|WEBHOOK_SECRET)\s*=/);
});
