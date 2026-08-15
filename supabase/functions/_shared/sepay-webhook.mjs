export const MAX_BODY_BYTES = 64 * 1024;
export const MAX_TIMESTAMP_SKEW_SECONDS = 300;

export function constantTimeEqual(left, right) {
  if (!(left instanceof Uint8Array) || !(right instanceof Uint8Array) || left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index += 1) difference |= left[index] ^ right[index];
  return difference === 0;
}

export function hex(bytes) {
  return Array.from(bytes).map((value) => value.toString(16).padStart(2, "0")).join("");
}

export async function verifyHmac(raw, timestamp, signature, secret,
  nowSeconds = Math.floor(Date.now() / 1000)) {
  if (!(raw instanceof Uint8Array) || !/^\d{10}$/.test(timestamp)
    || !/^sha256=[0-9a-f]{64}$/i.test(signature) || typeof secret !== "string" || secret.length < 32) return false;
  if (Math.abs(nowSeconds - Number(timestamp)) > MAX_TIMESTAMP_SKEW_SECONDS) return false;
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret),
    { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const prefix = new TextEncoder().encode(`${timestamp}.`);
  const signed = new Uint8Array(prefix.length + raw.length);
  signed.set(prefix); signed.set(raw, prefix.length);
  const digest = new Uint8Array(await crypto.subtle.sign("HMAC", key, signed));
  const expected = new TextEncoder().encode(`sha256=${hex(digest)}`);
  return constantTimeEqual(expected, new TextEncoder().encode(signature.toLowerCase()));
}

function bounded(value, maximum) {
  return typeof value === "string" && value.length <= maximum ? value : "";
}

function transactionDate(value) {
  const raw = bounded(value, 32);
  if (!/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/.test(raw)) return "";
  const date = new Date(`${raw.replace(" ", "T")}+07:00`);
  return Number.isNaN(date.valueOf()) ? "" : date.toISOString();
}

export function normalizeSePayPayload(body) {
  if (!body || typeof body !== "object" || Array.isArray(body)) return null;
  const providerTransactionId = String(body.id ?? "");
  const gateway = bounded(body.gateway, 100);
  const date = transactionDate(body.transactionDate);
  const accountNumber = bounded(body.accountNumber, 64);
  const transferType = bounded(body.transferType, 3);
  const amountVnd = Number(body.transferAmount);
  const content = bounded(body.content, 512);
  const documentedCode = bounded(body.code, 64).toUpperCase();
  const contentCode = content.toUpperCase().match(/\bAMS[A-Z0-9]{12,20}\b/)?.[0] ?? "";
  const paymentCode = documentedCode || contentCode;
  if (!/^[A-Za-z0-9_.:-]{1,128}$/.test(providerTransactionId) || !gateway || !date
    || !/^[A-Za-z0-9]{4,64}$/.test(accountNumber) || !["in", "out"].includes(transferType)
    || !Number.isSafeInteger(amountVnd) || amountVnd <= 0
    || (paymentCode && !/^AMS[A-Z0-9]{12,20}$/.test(paymentCode))) return null;
  return { providerTransactionId, referenceCode: bounded(body.referenceCode, 255), gateway,
    transactionDate: date, transferType, amountVnd, paymentCode, content, accountNumber };
}
