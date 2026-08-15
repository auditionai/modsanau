import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import { extname, join, normalize } from "node:path";

const root = join(import.meta.dirname, "..", "web");
const port = Number(process.env.PLAN107_EVIDENCE_PORT || 4177);
let orderState = "waiting_payment";

const products = [
  { productId: "evidence_subscription_30d", type: "subscription", displayName: "Gói 30 ngày", priceVnd: 99000, durationDays: 30, creditAmount: null, sortOrder: 1 },
  { productId: "evidence_credits_500", type: "credits", displayName: "500 Credits", priceVnd: 50000, durationDays: null, creditAmount: 500, sortOrder: 2 },
];
let selectedProduct = products[0];

const order = () => ({
  orderId: "10700000-0000-0000-0000-000000000001",
  orderCode: "AMS0123456789ABCDEF",
  status: orderState,
  productName: selectedProduct.displayName,
  productType: selectedProduct.type,
  priceVnd: selectedProduct.priceVnd,
  durationDays: selectedProduct.durationDays,
  creditAmount: selectedProduct.creditAmount,
  currency: "VND",
  paymentContent: "AMS0123456789ABCDEF",
  expiresAt: "2026-08-15T13:30:00+07:00",
  bankCode: "VCB",
  accountNumber: "0123456789",
  accountHolder: "AUDITION AI MOD STUDIO",
  qrUrl: `https://vietqr.app/img?acc=0123456789&bank=VCB&amount=${selectedProduct.priceVnd}&des=AMS0123456789ABCDEF&template=compact`,
});

async function readJson(request) {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  return JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}");
}

function sendJson(response, value, status = 200) {
  response.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
  response.end(JSON.stringify(value));
}

const types = { ".html": "text/html", ".css": "text/css", ".js": "text/javascript", ".png": "image/png", ".svg": "image/svg+xml", ".webp": "image/webp" };

createServer(async (request, response) => {
  const url = new URL(request.url, `http://127.0.0.1:${port}`);
  if (url.pathname === "/app/config") return sendJson(response, { supabaseUrl: `http://127.0.0.1:${port}`, publishableKey: "evidence-only" });
  if (url.pathname === "/admin/config") return sendJson(response, { supabaseUrl: `http://127.0.0.1:${port}`, publishableKey: "evidence-only" });
  if (url.pathname === "/auth/v1/token") return sendJson(response, { access_token: "evidence-access", refresh_token: "evidence-refresh", expires_in: 3600, user: { id: "10700000-0000-0000-0000-000000000010", email: "owner@evidence.local" } });
  if (url.pathname === "/rest/v1/rpc/admin_portal_api") {
    const { action } = await readJson(request);
    if (action === "session") return sendJson(response, { admin: { adminUserId: "10700000-0000-0000-0000-000000000010", email: "owner@evidence.local", displayLabel: "Plan 107 Owner", role: "owner", mfaState: "MFA_VERIFIED" } });
    if (action === "analytics") return sendJson(response, { grossRevenueVnd: 0, successfulPayments: 0, creditsGranted: 0, daily: [] });
    if (action === "dashboard") return sendJson(response, { totals: {}, recentTransactions: [], recentAudit: [] });
    return sendJson(response, { items: [] });
  }
  if (url.pathname === "/functions/v1/payments/catalog") return sendJson(response, { products });
  if (url.pathname === "/functions/v1/payments/admin/metrics") return sendJson(response, { revenueConfirmedVnd: 248000, paidOrders: 3, pendingOrders: 2, reviewRequired: 1, subscriptionSales: 2, creditSales: 1 });
  if (url.pathname === "/functions/v1/payments/admin/orders") return sendJson(response, { orders: [{ orderId: "10700000-0000-0000-0000-000000000001", orderCode: "AMS0123456789ABCDEF", deviceCode: "AMS-2345-6789-ABCD", productName: "Gói 30 ngày", productType: "subscription", priceVnd: 99000, status: "fulfilled", createdAt: "2026-08-15T05:00:00Z", paidAt: "2026-08-15T05:02:00Z", providerReference: "FT107", reviewReason: null }] });
  if (url.pathname === "/functions/v1/payments/admin/products") return sendJson(response, { products: products.map((product, index) => ({ ...product, paymentProductId: `10700000-0000-0000-0000-00000000002${index}`, productType: product.type, active: true, version: 1, effectiveFrom: "2026-08-15T00:00:00Z", effectiveTo: null })) });
  if (url.pathname === "/functions/v1/payments/admin/orders/10700000-0000-0000-0000-000000000001") return sendJson(response, { order: { payment_order_id: "10700000-0000-0000-0000-000000000001", order_code: "AMS0123456789ABCDEF", status: "fulfilled", product_name: "Gói 30 ngày", price_vnd: 99000 }, transaction: { provider_transaction_id: "314159", amount_vnd: 99000 }, fulfillment: { authority_reference: "SEPAY_ORDER:10700000-0000-0000-0000-000000000001" } });
  if (url.pathname === "/functions/v1/payments/orders" && request.method === "POST") {
    const body = await readJson(request);
    selectedProduct = products.find(product => product.productId === body.product_id) || products[0];
    orderState = "waiting_payment";
    return sendJson(response, order(), 201);
  }
  if (url.pathname.startsWith("/functions/v1/payments/orders/") && request.method === "GET") return sendJson(response, order());
  if (url.pathname.startsWith("/functions/v1/payments/orders/") && request.method === "DELETE") { orderState = "cancelled"; return sendJson(response, order()); }
  if (url.pathname === "/__evidence/state") { orderState = url.searchParams.get("value") || "waiting_payment"; return sendJson(response, { orderState }); }

  const relative = url.pathname === "/" ? "index.html" : decodeURIComponent(url.pathname.slice(1)) + (url.pathname.endsWith("/") ? "index.html" : "");
  const candidate = normalize(join(root, relative));
  if (!candidate.startsWith(normalize(root))) { response.writeHead(404); return response.end(); }
  try {
    if (!(await stat(candidate)).isFile()) throw new Error("not file");
    response.writeHead(200, { "Content-Type": `${types[extname(candidate)] || "application/octet-stream"}; charset=utf-8` });
    response.end(await readFile(candidate));
  } catch { response.writeHead(404); response.end("Not found"); }
}).listen(port, "127.0.0.1", () => console.log(`PLAN 107 UI evidence: http://127.0.0.1:${port}`));
