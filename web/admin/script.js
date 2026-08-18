"use strict";
const state = {
  session: null,
  config: null,
  accessToken: "",
  refreshToken: "",
  csrfToken: crypto.randomUUID(),
  view: "dashboard",
  commerceTab: "analytics",
  days: 30,
  analytics: null,
  dashboard: null,
  users: [],
  devices: [],
  transactions: [],
  paymentOrders: [],
  paymentProducts: [],
  paymentMetrics: null,
  aiModels: [],
  packages: [],
  mods: [],
  gifts: [],
  admins: [],
  audit: [],
  busy: 0,
  controller: null,
};
const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const views = {
  dashboard: ["TỔNG QUAN", "Tổng quan vận hành"],
  revenue: ["NẠP TIỀN", "Thống kê nạp tiền"],
  users: ["NGƯỜI DÙNG", "Quản lý người dùng"],
  devices: ["THIẾT BỊ", "Quản lý thiết bị"],
  payments: ["THANH TOÁN", "Quản lý thanh toán SePay"],
  transactions: ["GIAO DỊCH", "Tra cứu giao dịch"],
  packages: ["GÓI NẠP", "Quản lý gói nạp"],
  commerce: ["DOANH THU", "Doanh thu và thanh toán"],
  "ai-models": ["AI STUDIO", "Model AI và bảng giá"],
  mods: ["MOD", "Quản lý Mod"],
  giftcodes: ["GIFT CODE", "Quản lý gift code"],
  admins: ["TÀI KHOẢN ADMIN", "Kiểm soát truy cập"],
  audit: ["NHẬT KÝ", "Nhật ký bảo mật"],
};
const labels = {
  active: "Hoạt động",
  online: "Trực tuyến",
  recent: "Gần đây",
  offline: "Ngoại tuyến",
  blocked: "Đã khóa",
  revoked: "Đã thu hồi",
  suspended: "Tạm khóa",
  deactivated: "Vô hiệu hóa",
  expired: "Hết hạn",
  none: "Chưa có",
  duration: "Thời hạn",
  credits: "Credits",
  hybrid: "Hybrid",
  paid: "Đã thanh toán",
  fulfilling: "Đang cấp quyền",
  fulfilled: "Đã hoàn tất",
  pending: "Đang chờ thanh toán",
  cancelled: "Đã hủy",
  failed: "Thất bại",
  disabled: "Đã ẩn",
};
function escapeHtml(value) {
  return String(value ?? "").replace(
    /[&<>'"]/g,
    (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[
        c
      ],
  );
}
function uuid() {
  return crypto.randomUUID();
}
function number(v) {
  return new Intl.NumberFormat("vi-VN").format(Number(v || 0));
}
function providerPrice(value) {
  if (!Array.isArray(value) || value.length === 0) return "—";
  const values = value.map((item) => {
    if (!item || typeof item !== "object") return "";
    const row = item;
    return row.credits ?? row.cost ?? row.price ?? "";
  }).filter((item) => item !== "");
  return values.length ? escapeHtml(values.join(" · ")) : "—";
}
function settingText(value) {
  if (!value || typeof value !== "object") return "Mặc định";
  const entries = Object.entries(value).filter(([key]) => !isQualitySetting(key));
  return entries.length ? entries.map(([key, item]) => `${key}=${item}`).join(" · ") : "Mặc định";
}
function isQualitySetting(key) {
  return /^(quality|resolution|image_size|imageSize|output_resolution|outputResolution|size|dimensions?)$/i.test(String(key));
}
function qualityText(value) {
  if (!value || typeof value !== "object") return "Mặc định";
  const entries = Object.entries(value).filter(([key]) => isQualitySetting(key));
  if (!entries.length) return "Mặc định";
  return entries.map(([key, item]) => {
    const label = /resolution|size|dimension/i.test(key) ? "Độ phân giải" : "Chất lượng";
    return `${label}: ${item}`;
  }).join(" · ");
}
function isAllowedAiModel(model) {
  const value = `${model?.modelId ?? model?.id ?? ""} ${model?.modelName ?? model?.name ?? ""}`
    .toLowerCase().replace(/[._]/g, " ");
  return /\bgpt(?:[- ]?image)?[- ]?2\b/.test(value)
    || /\bnano[- ]?banana[- ]?pro\b/.test(value)
    || /\b(?:image|imagen)[- ]?4\b/.test(value)
    || /\bflux[- ]?2[- ]?pro\b/.test(value);
}
function money(v, currency = "vnd") {
  return new Intl.NumberFormat("vi-VN", {
    style: "currency",
    currency: String(currency).toUpperCase(),
    maximumFractionDigits: 0,
  }).format(Number(v || 0));
}
function date(v) {
  if (!v) return "—";
  return new Intl.DateTimeFormat("vi-VN", {
    dateStyle: "short",
    timeStyle: "short",
  }).format(new Date(v));
}
function short(v, n = 13) {
  const s = String(v ?? "");
  return s.length > n ? s.slice(0, n) + "…" : s;
}
function badge(v) {
  const x = String(v || "none").toLowerCase();
  return `<span class="badge ${escapeHtml(x)}">${escapeHtml(labels[x] || x)}</span>`;
}
function empty(cols, text) {
  return `<tr><td colspan="${cols}" class="empty-state">${escapeHtml(text)}</td></tr>`;
}
function toast(message, type = "success") {
  const el = document.createElement("div");
  el.className = `toast ${type}`;
  el.textContent = message;
  $("[data-toasts]").append(el);
  setTimeout(() => el.remove(), 4500);
}
function setBusy(on) {
  state.busy += on ? 1 : -1;
  state.busy = Math.max(0, state.busy);
  $("[data-refresh]").disabled = state.busy > 0;
}
function showError(message) {
  const box = $("[data-global-state]");
  $("p", box).textContent = message;
  box.hidden = false;
}
function clearError() {
  $("[data-global-state]").hidden = true;
}
async function ensureConfig() {
  if (state.config) return state.config;
  const response = await fetch("/admin/config", { cache: "no-store" });
  if (!response.ok)
    throw new Error(
      "Netlify chưa có SUPABASE_URL và SUPABASE_PUBLISHABLE_KEY.",
    );
  state.config = await response.json();
  return state.config;
}
function rememberTokens(tokens) {
  state.accessToken = tokens?.access_token || "";
  state.refreshToken = tokens?.refresh_token || "";
  state.csrfToken = crypto.randomUUID();
}
async function authRequest(route, body, token = "") {
  const config = await ensureConfig();
  const headers = {
    "Content-Type": "application/json",
    apikey: config.publishableKey,
  };
  if (token) headers.Authorization = `Bearer ${token}`;
  return fetch(`${config.supabaseUrl}/auth/v1/${route}`, {
    method: "POST",
    headers,
    body: JSON.stringify(body || {}),
    cache: "no-store",
  });
}
async function refreshSession() {
  if (!state.refreshToken) return false;
  const response = await authRequest("token?grant_type=refresh_token", {
    refresh_token: state.refreshToken,
  });
  if (!response.ok) {
    rememberTokens(null);
    return false;
  }
  rememberTokens(await response.json());
  return true;
}
let activeOptions = null;
function optionHeader(name) {
  return new Headers(activeOptions?.headers || {}).get(name) || "";
}
function rpcRoute(path, method, body) {
  const url = new URL(path, location.origin),
    parts = url.pathname.split("/").filter(Boolean);
  const payload = body ? JSON.parse(body) : {};
  for (const [key, value] of url.searchParams) payload[key] = value;
  if (url.pathname === "/v1/admin/session") return ["session", payload];
  if (url.pathname === "/v1/admin/analytics") return ["analytics", payload];
  if (url.pathname === "/v1/admin/dashboard") return ["dashboard", payload];
  if (url.pathname === "/v1/admin/users" && method === "GET")
    return ["users", payload];
  if (parts[2] === "users" && method === "PATCH")
    return ["user_update", { ...payload, userId: parts[3] }];
  if (url.pathname === "/v1/admin/devices" && method === "GET")
    return ["devices", payload];
  if (parts[2] === "devices" && method === "POST")
    return [
      "device_status",
      {
        ...payload,
        deviceProfileId: parts[3],
        status: parts[4] === "unblock" ? "active" : parts[4],
        reason: optionHeader("X-Admin-Reason"),
        correlationId: optionHeader("X-Admin-Correlation-Id"),
      },
    ];
  if (url.pathname === "/v1/admin/transactions")
    return ["transactions", payload];
  if (url.pathname === "/v1/admin/packages" && method === "GET")
    return ["packages", payload];
  if (url.pathname === "/v1/admin/packages" && method === "POST")
    return ["package_save", payload];
  if (parts[2] === "packages" && parts[4] === "archive")
    return ["package_archive", { ...payload, packageId: parts[3] }];
  if (url.pathname === "/v1/admin/gift-codes" && method === "GET")
    return ["gift_codes", payload];
  if (url.pathname === "/v1/admin/gift-codes" && method === "POST")
    return ["gift_create", payload];
  if (parts[2] === "gift-codes" && parts[4] === "revoke")
    return ["gift_revoke", { ...payload, giftCodeId: parts[3] }];
  if (url.pathname === "/v1/admin/admins" && method === "GET")
    return ["admins", payload];
  if (parts[2] === "admins" && method === "PATCH")
    return ["admin_update", { ...payload, adminUserId: parts[3] }];
  if (url.pathname === "/v1/admin/audit") return ["audit", payload];
  throw new Error("ADMIN_ACTION_UNKNOWN");
}
async function rpc(action, payload, retry = true) {
  const config = await ensureConfig();
  const response = await fetch(
    `${config.supabaseUrl}/rest/v1/rpc/admin_portal_api`,
    {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        apikey: config.publishableKey,
        Authorization: `Bearer ${state.accessToken}`,
        "X-CSRF-Token": state.csrfToken,
      },
      body: JSON.stringify({ action, payload }),
      cache: "no-store",
      signal: activeOptions?.signal || state.controller?.signal,
    },
  );
  let result = null;
  try {
    result = await response.json();
  } catch {}
  if (response.status === 401 && retry && (await refreshSession()))
    return rpc(action, payload, false);
  if (!response.ok) {
    if (response.status === 401 || response.status === 403) showLogin();
    const code = result?.message || result?.code || "REQUEST_FAILED";
    throw new Error(errorMessage(code, response.status));
  }
  return result;
}
async function paymentApi(route, options = {}, retry = true) {
  const config = await ensureConfig();
  let response;
  try {
    response = await fetch(
      `${config.supabaseUrl}/functions/v1/payments/admin/${route}`,
      {
        ...options,
        headers: {
          "Content-Type": "application/json",
          apikey: config.publishableKey,
          Authorization: `Bearer ${state.accessToken}`,
          "X-CSRF-Token": state.csrfToken,
          ...(options.headers || {}),
        },
        cache: "no-store",
        signal: state.controller?.signal,
      },
    );
  } catch (error) {
    if (error?.name === "AbortError") throw error;
    throw new Error(
      "Không thể kết nối dịch vụ doanh thu. Hãy tải lại trang; nếu vẫn lỗi, kiểm tra cấu hình máy chủ.",
    );
  }
  let result = null;
  try {
    result = await response.json();
  } catch {}
  if (response.status === 401 && retry && (await refreshSession()))
    return paymentApi(route, options, false);
  if (!response.ok) {
    if (response.status === 401) showLogin();
    throw new Error(
      errorMessage(
        result?.error || "PAYMENT_ADMIN_REQUEST_FAILED",
        response.status,
      ),
    );
  }
  return result;
}
async function aiModelApi(options = {}, retry = true) {
  const config = await ensureConfig();
  const response = await fetch(`${config.supabaseUrl}/functions/v1/ai-model-admin`, {
    ...options,
    headers: {
      "Content-Type": "application/json",
      apikey: config.publishableKey,
      Authorization: `Bearer ${state.accessToken}`,
      "X-CSRF-Token": state.csrfToken,
      ...(options.headers || {}),
    },
    cache: "no-store",
    signal: state.controller?.signal,
  });
  let result = null;
  try { result = await response.json(); } catch {}
  if (response.status === 401 && retry && (await refreshSession())) return aiModelApi(options, false);
  if (!response.ok) throw new Error(errorMessage(result?.error || "AI_MODEL_REQUEST_FAILED", response.status));
  return result;
}
async function modApi(body, retry = true) {
  const config = await ensureConfig();
  let response;
  try {
    response = await fetch(
      `${config.supabaseUrl}/functions/v1/mod-product-admin`,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          apikey: config.publishableKey,
          Authorization: `Bearer ${state.accessToken}`,
        },
        body: JSON.stringify(body),
        cache: "no-store",
        signal: state.controller?.signal,
      },
    );
  } catch (error) {
    if (error?.name === "AbortError") throw error;
    throw new Error(
      "Không thể kết nối dịch vụ Mod. Hãy tải lại trang; nếu vẫn lỗi, kiểm tra cấu hình máy chủ.",
    );
  }
  let result = null;
  try {
    result = await response.json();
  } catch {}
  if (response.status === 401 && retry && (await refreshSession()))
    return modApi(body, false);
  if (!response.ok) {
    if (response.status === 401) showLogin();
    throw new Error(
      errorMessage(
        result?.error || "MOD_PRODUCT_REQUEST_FAILED",
        response.status,
      ),
    );
  }
  return result;
}
async function api(path, options = {}) {
  const method = (options.method || "GET").toUpperCase();
  if (path === "/v1/admin/session/login") {
    const input = JSON.parse(options.body || "{}");
    const response = await authRequest("token?grant_type=password", input);
    if (!response.ok)
      throw new Error(errorMessage("ADMIN_LOGIN_REJECTED", response.status));
    rememberTokens(await response.json());
    return rpc("session", {});
  }
  if (path === "/v1/admin/session/logout") {
    try {
      if (state.accessToken) await authRequest("logout", {}, state.accessToken);
    } finally {
      rememberTokens(null);
    }
    return { code: "ADMIN_SIGNED_OUT" };
  }
  activeOptions = options;
  try {
    const [action, payload] = rpcRoute(path, method, options.body);
    return await rpc(action, payload);
  } finally {
    activeOptions = null;
  }
}
function errorMessage(code, status) {
  const map = {
    ADMIN_LOGIN_REJECTED: "Email, mật khẩu hoặc quyền quản trị không hợp lệ.",
    ADMIN_ACCESS_REQUIRED: "Tài khoản không có quyền thực hiện thao tác này.",
    ADMIN_CSRF_REJECTED: "Phiên bảo mật không hợp lệ. Hãy đăng nhập lại.",
    ADMIN_PORTAL_UNAVAILABLE:
      "Backend quản trị chưa được cấu hình hoặc đang gián đoạn.",
    RATE_LIMIT_EXCEEDED: "Thao tác quá nhanh. Vui lòng chờ một phút.",
    HTTPS_REQUIRED: "Kết nối quản trị bắt buộc HTTPS.",
    MOD_PRODUCT_AUTH_REQUIRED: "Phiên đăng nhập đã hết hạn. Hãy đăng nhập lại.",
    MOD_PRODUCT_ADMIN_REQUIRED:
      "Tài khoản này chưa có quyền quản lý Mod hoặc chưa xác minh MFA.",
    MOD_PRODUCT_LIST_FAILED:
      "Không thể tải danh sách Mod từ máy chủ. Hãy thử lại sau.",
    MOD_PRODUCT_UPDATE_FAILED: "Không thể lưu thay đổi Mod. Hãy thử lại.",
    MOD_PRODUCT_NOT_FOUND: "Không tìm thấy Mod cần cập nhật.",
    MOD_PRODUCT_UPLOAD_URL_FAILED:
      "Máy chủ chưa cấp được đường dẫn tải file lên. Hãy thử lại sau.",
    MOD_PRODUCT_ARCHIVE_UPLOAD_FAILED:
      "Tải file Mod lên kho lưu trữ thất bại. Kiểm tra kết nối rồi thử lại.",
    MOD_PRODUCT_ARCHIVE_NOT_READY:
      "File Mod chưa sẵn sàng trên kho lưu trữ. Hãy chờ ít giây rồi tải lại.",
    MOD_PRODUCT_PUBLISH_FAILED:
      "File đã tải lên nhưng chưa thể phát hành. Hãy thử lại, không cần tải lại file.",
    PAYMENT_AUTH_REQUIRED: "Phiên đăng nhập đã hết hạn. Hãy đăng nhập lại.",
    PAYMENT_ADMIN_REQUEST_FAILED:
      "Không thể tải dữ liệu doanh thu. Hãy thử lại sau.",
    PAYMENT_BACKEND_UNAVAILABLE:
      "Dịch vụ doanh thu đang chưa sẵn sàng. Hãy thử lại sau.",
    PAYMENT_ROUTE_NOT_FOUND:
      "Phiên bản dịch vụ doanh thu không khớp. Cần triển khai lại máy chủ.",
    ORIGIN_NOT_ALLOWED:
      "Trang quản trị này chưa được máy chủ cho phép kết nối. Cần cập nhật cấu hình máy chủ.",
  };
  return map[code] || `Yêu cầu thất bại (${code || status}).`;
}
function showLogin() {
  state.session = null;
  $("[data-app]").hidden = true;
  $("[data-login-screen]").hidden = false;
  document.body.classList.remove("app-active");
  setTimeout(() => $("[data-login-form] input")?.focus(), 0);
}
function showApp(session) {
  state.session = session;
  $("[data-login-screen]").hidden = true;
  $("[data-app]").hidden = false;
  document.body.classList.add("app-active");
  const admin = session.admin || {};
  $("[data-admin-name]").textContent =
    admin.displayLabel || admin.email || "Admin";
  $("[data-admin-email]").textContent = admin.email || "";
  $("[data-admin-role]").textContent = admin.role || "";
  $("[data-avatar]").textContent = (admin.displayLabel || admin.email || "A")
    .slice(0, 1)
    .toUpperCase();
  $$(".owner-only").forEach((el) => (el.hidden = admin.role !== "owner"));
  $$(".operator-only").forEach((el) => (el.hidden = admin.role === "auditor"));
  $("[data-api-status]").textContent = "Supabase đã kết nối";
  navigate("dashboard");
}
async function restoreSession() {
  try {
    await ensureConfig();
    if (!state.accessToken && !(await refreshSession())) return showLogin();
    const session = await api("/v1/admin/session");
    showApp(session);
  } catch (error) {
    showLogin();
    if (error.message.includes("Netlify")) showError(error.message);
  }
}
function applyCommerceTab() {
  $$(".commerce-panel").forEach((panel) => {
    panel.hidden = panel.dataset.commerceTab !== state.commerceTab;
  });
  $$("[data-commerce-tab-button]").forEach((button) => {
    const active = button.dataset.commerceTabButton === state.commerceTab;
    button.classList.toggle("is-active", active);
    button.setAttribute("aria-selected", String(active));
  });
}
function installCommerceTabs() {
  $$(".commerce-panel").forEach((panel) => {
    const heading = $(".section-heading", panel);
    if (!heading || $(".commerce-tabs", panel)) return;
    heading.insertAdjacentHTML(
      "beforebegin",
      `<nav class="commerce-tabs" aria-label="Doanh thu"><button type="button" data-commerce-tab-button="analytics">Tổng quan</button><button type="button" data-commerce-tab-button="orders">Đơn thanh toán</button><button type="button" data-commerce-tab-button="transactions">Giao dịch</button></nav>`,
    );
  });
}
function navigate(view) {
  if (!views[view]) return;
  if (view === "admins" && state.session?.admin?.role !== "owner") return;
  state.view = view;
  $$("[data-pane]").forEach((el) =>
    el.classList.toggle("is-active", el.dataset.pane === view),
  );
  if (view === "commerce") applyCommerceTab();
  $$("[data-view]").forEach((el) =>
    el.classList.toggle("is-active", el.dataset.view === view),
  );
  $("[data-breadcrumb]").textContent = views[view][0];
  $("[data-page-title]").textContent = views[view][1];
  $("[data-sidebar]").classList.remove("is-open");
  clearError();
  loadView(view).catch((error) => showError(error.message));
}
async function loadView(view) {
  state.controller?.abort();
  state.controller = new AbortController();
  setBusy(true);
  try {
    if (view === "dashboard")
      await Promise.all([loadAnalytics(), loadDashboard()]);
    if (view === "commerce") {
      if (state.commerceTab === "analytics") await loadAnalytics();
      if (state.commerceTab === "orders") await loadPayments();
      if (state.commerceTab === "transactions") await loadTransactions();
    }
    if (view === "ai-models") await loadAiModels();
    if (view === "users") await loadUsers();
    if (view === "devices") await loadDevices();
    if (view === "payments") await loadPayments();
    if (view === "transactions") await loadTransactions();
    if (view === "packages") await loadPackages();
    if (view === "mods") await loadMods();
    if (view === "giftcodes") await loadGifts();
    if (view === "admins") await loadAdmins();
    if (view === "audit") await loadAudit();
  } finally {
    setBusy(false);
  }
}
async function loadAnalytics() {
  state.analytics = await api(`/v1/admin/analytics?days=${state.days}`);
  renderAnalytics();
}
async function loadDashboard() {
  state.dashboard = await api("/v1/admin/dashboard");
  renderRecentAudit(state.dashboard.recentAudit || []);
}
function kpi(label, value, caption, icon, color) {
  return `<article class="kpi-card" style="--glow:${color}"><span class="kpi-icon">${icon}</span><p>${label}</p><strong>${value}</strong><small>${caption}</small></article>`;
}
function renderAnalytics() {
  const a = state.analytics;
  if (!a) return;
  $("[data-kpis]").innerHTML = [
    kpi(
      "Tổng người dùng",
      number(a.totalUsers),
      `+${number(a.newUsers30Days)} trong 30 ngày`,
      `◉`,
      "rgba(139,92,246,.18)",
    ),
    kpi(
      "Doanh thu xác minh",
      money(a.revenueMinor, a.currency),
      `${number(a.totalTransactions)} giao dịch`,
      `↗`,
      "rgba(54,211,153,.16)",
    ),
    kpi(
      "Thiết bị hoạt động",
      number(a.activeDevices),
      `${number(a.blockedDevices + a.revokedDevices)} cần chú ý`,
      `▣`,
      "rgba(56,189,248,.16)",
    ),
    kpi(
      "Gói còn hiệu lực",
      number(a.activeSubscriptions),
      `${number(a.expiringSubscriptions7Days)} sắp hết hạn`,
      `◇`,
      "rgba(251,191,36,.16)",
    ),
  ].join("");
  $("[data-revenue-kpis]").innerHTML = [
    kpi("Doanh thu", money(a.revenueMinor, a.currency), "Đã xác minh", "↗"),
    kpi("Giao dịch", number(a.totalTransactions), "Payment events", "⇄"),
    kpi("Credits đã bán", number(a.creditsSold), "Qua thanh toán", "✦"),
  ].join("");
  $("[data-revenue-total]").textContent = money(a.revenueMinor, a.currency);
  $("[data-expiring]").textContent = number(a.expiringSubscriptions7Days);
  renderBars($("[data-revenue-chart]"), a.daily, "revenueMinor", a.currency);
  renderBars($("[data-topup-chart]"), a.daily, "revenueMinor", a.currency);
  const total = Math.max(
      1,
      a.activeDevices + a.blockedDevices + a.revokedDevices,
    ),
    active = (a.activeDevices / total) * 100,
    blocked = ((a.activeDevices + a.blockedDevices) / total) * 100;
  const donut = $("[data-device-donut]");
  donut.style.setProperty("--active", active + "%");
  donut.style.setProperty("--blocked", blocked + "%");
  $("span", donut).textContent = number(total);
  $("[data-device-legend]").innerHTML = [
    ["#36d399", "Hoạt động", a.activeDevices],
    ["#fbbf24", "Đã khóa", a.blockedDevices],
    ["#fb7185", "Đã thu hồi", a.revokedDevices],
  ]
    .map(
      (x) =>
        `<div><i style="background:${x[0]}"></i><span>${x[1]}</span><b>${number(x[2])}</b></div>`,
    )
    .join("");
}
function renderBars(el, items, key, currency) {
  if (!el) return;
  const max = Math.max(1, ...items.map((x) => Number(x[key] || 0)));
  el.innerHTML = items
    .map((x) => {
      const height = Math.max(1, (Number(x[key] || 0) / max) * 100),
        label = `${x.day}: ${money(x[key], currency)}`;
      return `<div class="bar-wrap" data-label="${escapeHtml(label)}" style="--height:${height}%"><span class="bar" style="height:${height}%"></span></div>`;
    })
    .join("");
}
function renderRecentAudit(items) {
  $("[data-recent-audit]").innerHTML = items.length
    ? items
        .slice(0, 5)
        .map(
          (x) =>
            `<div class="activity-row"><i>✓</i><div><strong>${escapeHtml(x.eventType)}</strong><small>${escapeHtml(x.targetKind)} · ${short(x.targetId || "system")}</small></div><time>${date(x.eventAt)}</time></div>`,
        )
        .join("")
    : '<div class="empty-state">Chưa có hoạt động quản trị.</div>';
}
async function loadUsers() {
  const q = encodeURIComponent($("[data-user-search]").value.trim()),
    status = encodeURIComponent($("[data-user-status]").value);
  const data = await api(
    `/v1/admin/users?query=${q}&status=${status}&limit=100&offset=0`,
  );
  state.users = data.items || [];
  $("[data-users-count]").textContent = `${number(data.totalCount)} người dùng`;
  $("[data-users-body]").innerHTML = state.users.length
    ? state.users
        .map(
          (u) =>
            `<tr><td><strong>${escapeHtml(u.displayName || u.email || "Chưa có email")}</strong><small class="mono">${escapeHtml(u.userId)}</small></td><td><span class="mono">${escapeHtml(u.publicDeviceCode || "—")}</span><small>${escapeHtml(labels[u.deviceStatus] || u.deviceStatus)}</small></td><td><strong>${number(u.availableCredits)} credits</strong><small>${number(u.reservedCredits)} đang giữ</small></td><td>${badge(u.subscriptionStatus)}<small>${date(u.subscriptionExpiresAt)}</small></td><td>${badge(u.status)}</td><td><div class="row-actions"><button data-edit-user="${u.userId}">Chi tiết</button></div></td></tr>`,
        )
        .join("")
    : empty(6, "Không tìm thấy người dùng.");
}
async function loadDevices() {
  const q = encodeURIComponent($("[data-device-search]").value.trim()),
    status = encodeURIComponent($("[data-device-status]").value);
  const data = await api(
    `/v1/admin/devices?query=${q}&status=${status}&limit=100&offset=0`,
  );
  state.devices = data.items || [];
  $("[data-devices-count]").textContent = `${number(data.totalCount)} thiết bị`;
  $("[data-devices-body]").innerHTML = state.devices.length
    ? state.devices
        .map(
          (d) =>
            `<tr><td><strong>${escapeHtml(d.displayName || d.email || "Chưa có tên")}</strong><small>${escapeHtml(d.email || d.authUserId)}</small></td><td><strong class="mono">${escapeHtml(d.publicDeviceCode)}</strong><small class="mono">${escapeHtml(d.deviceId || "Chưa đăng nhập ứng dụng")}</small></td><td><strong>${escapeHtml(d.deviceDisplayName || d.platform || "—")}</strong><small>${escapeHtml(d.clientVersion || "—")} · ${escapeHtml(d.platform || "—")}</small></td><td>${badge(d.presenceStatus || "offline")}<small>${date(d.lastSeenAt)}</small></td><td>${badge(d.deviceStatus)}</td><td><div class="row-actions operator-only">${d.deviceStatus === "active" ? `<button data-device-action="block" data-id="${d.deviceProfileId}">Khóa</button>` : ""}${d.deviceStatus === "blocked" ? `<button data-device-action="unblock" data-id="${d.deviceProfileId}">Mở</button>` : ""}${d.deviceStatus !== "revoked" ? `<button data-device-action="revoke" data-id="${d.deviceProfileId}">Thu hồi</button>` : ""}</div></td></tr>`,
        )
        .join("")
    : empty(6, "Không tìm thấy thiết bị.");
  applyRoleVisibility();
}
async function loadTransactions() {
  const q = encodeURIComponent($("[data-transaction-search]").value.trim()),
    provider = encodeURIComponent($("[data-provider-filter]").value.trim());
  const data = await api(
    `/v1/admin/transactions?query=${q}&provider=${provider}&limit=100&offset=0`,
  );
  state.transactions = data.items || [];
  $("[data-transactions-count]").textContent =
    `${number(data.totalCount)} giao dịch`;
  $("[data-transactions-body]").innerHTML = state.transactions.length
    ? state.transactions
        .map(
          (t) =>
            `<tr><td><strong>${escapeHtml(t.provider)} · ${short(t.providerPaymentId, 18)}</strong><small class="mono" title="${escapeHtml(t.grantTransactionId)}">${escapeHtml(t.grantTransactionId)}</small></td><td><strong>${escapeHtml(t.email || "—")}</strong><small class="mono">${escapeHtml(t.userId)}</small></td><td>${escapeHtml(t.productId)}</td><td><strong>${money(t.amountMinor, t.currency)}</strong></td><td>${number(t.credits)}</td><td>${date(t.verifiedAt)}</td></tr>`,
        )
        .join("")
    : empty(6, "Không tìm thấy giao dịch.");
}
async function loadPayments() {
  const status = encodeURIComponent($("[data-payment-status]").value);
  const [metrics, ordersResult, productsResult] = await Promise.all([
    paymentApi("metrics"),
    paymentApi(`orders?status=${status}`),
    paymentApi("products"),
  ]);
  state.paymentMetrics = metrics;
  state.paymentOrders = ordersResult.orders || [];
  state.paymentProducts = productsResult.products || [];
  $("[data-payment-kpis]").innerHTML = [
    kpi(
      "Doanh thu xác nhận",
      money(metrics.revenueConfirmedVnd, "VND"),
      "Đơn đã fulfillment",
      "₫",
    ),
    kpi(
      "Đã nhận",
      number(metrics.paidOrders),
      "Paid / fulfilling / fulfilled",
      "✓",
    ),
    kpi("Đang chờ", number(metrics.pendingOrders), "Chưa nhận tiền", "…"),
    kpi(
      "Cần kiểm tra",
      number(metrics.reviewRequired),
      "Không tự fulfillment",
      "!",
    ),
  ].join("");
  $("[data-payments-count]").textContent =
    `${number(state.paymentOrders.length)} đơn`;
  $("[data-payments-body]").innerHTML = state.paymentOrders.length
    ? state.paymentOrders
        .map(
          (o) =>
            `<tr><td><strong class="mono">${escapeHtml(o.orderCode)}</strong><small>${escapeHtml(o.providerReference || "—")}</small></td><td class="mono">${escapeHtml(o.deviceCode)}</td><td><strong>${escapeHtml(o.productName)}</strong><small>${escapeHtml(o.productType)}</small></td><td>${money(o.priceVnd, "VND")}</td><td>${badge(o.status)}</td><td>${date(o.createdAt)}</td><td><button data-payment-detail="${o.orderId}">Chi tiết</button></td></tr>`,
        )
        .join("")
    : empty(7, "Chưa có đơn thanh toán.");
  $("[data-payment-products]").innerHTML = state.paymentProducts.length
    ? state.paymentProducts
        .map(
          (p) =>
            `<article class="package-card ${p.active ? "" : "is-archived"}">${badge(p.active ? "active" : "disabled")}<h3>${escapeHtml(p.displayName)}</h3><p>${escapeHtml(p.productType === "subscription" ? `${number(p.durationDays)} ngày` : `${number(p.creditAmount)} Credits`)}</p><div class="package-price"><strong>${money(p.priceVnd, "VND")}</strong><span>phiên bản ${number(p.version)}</span></div><div class="package-meta"><span class="mono">${escapeHtml(p.productId)}</span><span>#${number(p.sortOrder)}</span></div></article>`,
        )
        .join("")
    : '<div class="empty-state">Chưa có sản phẩm thanh toán.</div>';
  applyRoleVisibility();
}
async function loadAiModels() {
  const result = await aiModelApi();
  state.aiModels = (result.models || []).filter(isAllowedAiModel);
  $("[data-ai-models-count]").textContent = `${number(state.aiModels.length)} cấu hình`;
  $("[data-ai-models-body]").innerHTML = state.aiModels.length
    ? state.aiModels.map((model) => `<tr data-ai-model-row="${escapeHtml(model.pricingId)}">
        <td><strong>${escapeHtml(model.modelName)}</strong><small class="mono">${escapeHtml(model.modelId)}</small></td>
        <td class="quality-cell">${escapeHtml(qualityText(model.settings))}</td>
        <td><input data-ai-model-cost type="number" min="1" max="1000000" step="1" value="${escapeHtml(String(model.creditCost))}" aria-label="Giá Credits ${escapeHtml(model.name)}" /></td>
        <td>${model.tstCost == null ? "—" : escapeHtml(String(model.tstCost))}</td>
        <td class="mono setting-cell">${escapeHtml(settingText(model.settings))}</td>
        <td><label class="check-label"><input data-ai-model-active type="checkbox" ${model.active ? "checked" : ""} /> Đang phát hành</label></td>
        <td>v${number(model.pricingVersion)}</td>
        <td><input data-ai-model-reason maxlength="500" minlength="8" placeholder="Lý do thay đổi" aria-label="Lý do thay đổi ${escapeHtml(model.name)}" /></td>
        <td><button class="secondary-button operator-only" data-save-ai-model="${escapeHtml(model.pricingId)}">Lưu</button></td>
      </tr>`).join("")
    : empty(9, "Chưa đồng bộ cấu hình giá model ảnh từ TST.");
  applyRoleVisibility();
}
async function showPaymentDetail(orderId) {
  const detail = await paymentApi(`orders/${orderId}`);
  const order = detail.order || {},
    transaction = detail.transaction || {},
    fulfillment = detail.fulfillment || {},
    retry = $("[data-payment-retry]");
  $("[data-payment-detail-content]").innerHTML =
    `<dl class="detail-list"><div><dt>Mã đơn</dt><dd>${escapeHtml(order.order_code)}</dd></div><div><dt>Trạng thái</dt><dd>${badge(order.status)}</dd></div><div><dt>Snapshot</dt><dd>${escapeHtml(order.product_name)} · ${money(order.price_vnd, "VND")}</dd></div><div><dt>SePay transaction</dt><dd>${escapeHtml(transaction.provider_transaction_id || "—")}</dd></div><div><dt>Số nhận</dt><dd>${money(transaction.amount_vnd, "VND")}</dd></div><div><dt>Fulfillment</dt><dd>${escapeHtml(fulfillment.authority_reference || "—")}</dd></div></dl>`;
  retry.dataset.orderId = orderId;
  retry.hidden =
    state.session?.admin?.role !== "owner" ||
    !["paid", "fulfilling"].includes(order.status);
  $("[data-payment-detail-dialog]").showModal();
}
async function loadPackages() {
  const data = await api("/v1/admin/packages?limit=1000");
  state.packages = data.items || [];
  $("[data-packages-grid]").innerHTML = state.packages.length
    ? state.packages
        .map(
          (p) =>
            `<article class="package-card ${p.archivedAt ? "is-archived" : ""}">${badge(p.isActive ? "active" : "disabled")}<h3>${escapeHtml(p.displayName)}</h3><p>${escapeHtml(p.description || "Chưa có mô tả")}</p><div class="package-price"><strong>${money(p.amountMinor, p.currency)}</strong><span>/ ${p.durationDays ? number(p.durationDays) + " ngày" : number(p.credits) + " credits"}</span></div><div class="package-meta"><span class="mono">${escapeHtml(p.productId)}</span><span>#${p.sortOrder}</span></div><div class="package-actions operator-only"><button class="secondary-button" data-edit-package="${p.packageId}">Chỉnh sửa</button>${p.isActive ? `<button class="ghost-button" data-archive-package="${p.packageId}">Lưu trữ</button>` : ""}</div></article>`,
        )
        .join("")
    : '<div class="empty-state">Chưa có gói nạp nào.</div>';
  applyRoleVisibility();
}
async function loadMods() {
  const data = await modApi({ action: "list" });
  const templates = data.templates || [];
  state.mods = (data.mods || []).map((mod) => ({
    ...mod,
    templates: templates.filter(
      (template) =>
        template.game_id === mod.game_id && template.mod_id === mod.mod_id,
    ),
  }));
  $("[data-mods-grid]").innerHTML = state.mods.length
    ? state.mods
        .map((mod) => {
          const current =
            mod.templates.find((template) => template.is_current) ||
            mod.templates[0];
          const archive = current
            ? `${current.archive_file_name || "archive"} | ${number(current.archive_content_length || 0)} bytes | v${current.template_version}`
            : "Chưa có archive được phát hành";
          return `<article class="package-card ${mod.is_active ? "" : "is-archived"}">${badge(mod.is_active ? "active" : "disabled")}<h3>${escapeHtml(mod.display_name)}</h3><p>${escapeHtml(mod.description || "Chưa có mô tả")}</p><div class="package-meta"><span class="mono">${escapeHtml(mod.game_id)}/${escapeHtml(mod.mod_id)}</span><span>${escapeHtml(archive)}</span></div><div class="package-actions operator-only"><button class="secondary-button" data-edit-mod="${escapeHtml(mod.game_id)}:${escapeHtml(mod.mod_id)}">Chỉnh sửa</button></div></article>`;
        })
        .join("")
    : '<div class="empty-state">Chưa có sản phẩm Mod nào được phát hành.</div>';
  applyRoleVisibility();
}
async function loadGifts() {
  const data = await api("/v1/admin/gift-codes?limit=100&offset=0");
  state.gifts = data.items || [];
  $("[data-gifts-body]").innerHTML = state.gifts.length
    ? state.gifts
        .map((g) => {
          const status = g.disabledAt
            ? "disabled"
            : g.expiresAt && new Date(g.expiresAt) < new Date()
              ? "expired"
              : "active";
          const valueDisplay =
            g.durationDays && g.creditAmount
              ? `${number(g.durationDays)} ngày + ${number(g.creditAmount)} credits`
              : g.durationDays
                ? `${number(g.durationDays)} ngày`
                : g.creditAmount
                  ? `${number(g.creditAmount)} credits`
                  : "—";
          return `<tr><td><strong class="mono">${escapeHtml(g.codePrefix)}…</strong><small class="mono">${escapeHtml(g.giftCodeId)}</small></td><td>${escapeHtml(labels[g.kind] || g.kind)}</td><td>${valueDisplay}</td><td>${number(g.redemptionCount)} / ${number(g.maximumRedemptions)}</td><td>${date(g.expiresAt)}</td><td>${badge(status)}</td><td><div class="row-actions operator-only">${status === "active" ? `<button data-revoke-gift="${g.giftCodeId}">Thu hồi</button>` : ""}</div></td></tr>`;
        })
        .join("")
    : empty(7, "Chưa có gift code.");
  applyRoleVisibility();
}
async function loadAdmins() {
  const data = await api("/v1/admin/admins");
  state.admins = data.items || [];
  $("[data-admins-grid]").innerHTML = state.admins
    .map(
      (a) =>
        `<article class="admin-card"><div class="admin-card-head"><span class="avatar">${escapeHtml((a.displayLabel || a.email || "A")[0].toUpperCase())}</span><div><h3>${escapeHtml(a.displayLabel || a.email)}</h3><p>${escapeHtml(a.email)}</p></div></div><dl><div><dt>Vai trò</dt><dd>${badge(a.role)}</dd></div><div><dt>Trạng thái</dt><dd>${badge(a.isActive ? "active" : "disabled")}</dd></div><div><dt>MFA</dt><dd>${escapeHtml(a.mfaState)}</dd></div><div><dt>Lần cuối</dt><dd>${date(a.lastSeenAt)}</dd></div></dl>${a.adminUserId !== state.session.admin.adminUserId ? `<button class="secondary-button" data-toggle-admin="${a.adminUserId}">${a.isActive ? "Vô hiệu hóa" : "Kích hoạt"}</button>` : ""}</article>`,
    )
    .join("");
}
async function loadAudit() {
  const data = await api("/v1/admin/audit?limit=100&offset=0");
  state.audit = data.items || [];
  $("[data-audit-body]").innerHTML = state.audit.length
    ? state.audit
        .map(
          (a) =>
            `<tr><td>${date(a.eventAt)}</td><td><strong>${escapeHtml(a.eventType)}</strong><small>${escapeHtml(a.details || "")}</small></td><td class="mono">${escapeHtml(a.actorAdminUserId)}</td><td>${escapeHtml(a.targetKind)}<small class="mono">${escapeHtml(a.targetId || "—")}</small></td><td class="mono">${escapeHtml(a.correlationId)}</td></tr>`,
        )
        .join("")
    : empty(5, "Chưa có sự kiện audit.");
}
function applyRoleVisibility() {
  $$(".operator-only").forEach(
    (el) => (el.hidden = state.session?.admin?.role === "auditor"),
  );
}
function openUser(id) {
  const u = state.users.find((x) => x.userId === id);
  if (!u) return;
  const f = $("[data-user-form]");
  f.userId.value = u.userId;
  f.displayName.value = u.displayName || "";
  f.contactEmail.value = u.email || "";
  f.status.value = u.status;
  f.internalNote.value = "";
  f.reason.value = "";
  $("[data-user-dialog]").showModal();
}
function openPackage(id) {
  const p = state.packages.find((x) => x.packageId === id),
    f = $("[data-package-form]");
  f.reset();
  f.currency.value = "vnd";
  f.sortOrder.value = "0";
  f.isActive.checked = true;
  f.kind.value = "credits";
  $("[data-credits-field]").hidden = false;
  $("[data-days-field]").hidden = true;
  f.credits.required = true;
  f.durationDays.required = false;
  if (p) {
    f.packageId.value = p.packageId;
    f.productId.value = p.productId;
    f.displayName.value = p.displayName;
    f.description.value = p.description || "";
    f.amountMinor.value = p.amountMinor;
    f.currency.value = p.currency;
    const isSubscription = p.durationDays && p.durationDays > 0;
    f.kind.value = isSubscription ? "subscription" : "credits";
    if (isSubscription) {
      f.durationDays.value = p.durationDays;
      $("[data-credits-field]").hidden = true;
      $("[data-days-field]").hidden = false;
      f.credits.required = false;
      f.durationDays.required = true;
    } else {
      f.credits.value = p.credits;
      $("[data-credits-field]").hidden = false;
      $("[data-days-field]").hidden = true;
      f.credits.required = true;
      f.durationDays.required = false;
    }
    f.isActive.checked = p.isActive;
    f.sortOrder.value = p.sortOrder;
    $("[data-package-title]").textContent = "Chỉnh sửa gói nạp";
  } else {
    $("[data-package-title]").textContent = "Tạo gói nạp";
  }
  $("[data-package-dialog]").showModal();
}
function openMod(key) {
  const mod = state.mods.find(
    (item) => `${item.game_id}:${item.mod_id}` === key,
  );
  if (!mod) return;
  const form = $("[data-mod-form]");
  form.gameId.value = mod.game_id;
  form.modId.value = mod.mod_id;
  form.displayName.value = mod.display_name || "";
  form.description.value = mod.description || "";
  form.compatibilityInformation.value = mod.compatibility_information || "";
  form.isActive.checked = Boolean(mod.is_active);
  const template =
    mod.templates.find((item) => item.is_current) || mod.templates[0];
  $("[data-mod-archive]").textContent = template
    ? `Archive: ${template.archive_file_name}, phiên bản ${template.template_version}, ${number(template.archive_content_length || 0)} byte`
    : "Archive chưa được phát hành.";
  $("[data-mod-dialog]").showModal();
}
function confirmAction(title, message) {
  return new Promise((resolve) => {
    const d = $("[data-confirm-dialog]");
    $("[data-confirm-title]").textContent = title;
    $("[data-confirm-message]").textContent = message;
    $("[data-confirm-reason]").value = "";
    const done = () => {
      d.removeEventListener("close", done);
      resolve(
        d.returnValue === "confirm"
          ? $("[data-confirm-reason]").value.trim()
          : null,
      );
    };
    d.addEventListener("close", done);
    d.showModal();
  });
}
document.addEventListener("click", async (e) => {
  const b = e.target.closest("button");
  if (!b) return;
  if (b.matches("[data-dialog-close]")) {
    b.closest("dialog")?.close("cancel");
    return;
  }
  if (b.dataset.view) navigate(b.dataset.view);
  if (b.dataset.go) navigate(b.dataset.go);
  if (b.dataset.commerceTabButton) {
    state.commerceTab = b.dataset.commerceTabButton;
    applyCommerceTab();
    loadView("commerce").catch((error) => showError(error.message));
  }
  if (b.matches("[data-menu-toggle]"))
    $("[data-sidebar]").classList.toggle("is-open");
  if (b.matches("[data-admin-menu]"))
    $("[data-admin-popover]").hidden = !$("[data-admin-popover]").hidden;
  if (b.matches("[data-toggle-password]")) {
    const i = $("[data-login-form] input[name=password]");
    i.type = i.type === "password" ? "text" : "password";
    b.textContent = i.type === "password" ? "Hiện" : "Ẩn";
  }
  if (b.matches("[data-refresh],[data-retry]")) navigate(state.view);
  if (b.matches("[data-user-submit]"))
    loadUsers().catch((x) => showError(x.message));
  if (b.matches("[data-device-submit]"))
    loadDevices().catch((x) => showError(x.message));
  if (b.matches("[data-transaction-submit]"))
    loadTransactions().catch((x) => showError(x.message));
  if (b.dataset.editUser) openUser(b.dataset.editUser);
  if (b.dataset.saveAiModel) {
    const row = b.closest("tr");
    const current = state.aiModels.find((model) => model.pricingId === b.dataset.saveAiModel);
    if (!row || !current) return;
    const reason = $("[data-ai-model-reason]", row).value.trim();
    if (reason.length < 8) {
      toast("Nhập lý do thay đổi ít nhất 8 ký tự.", "error");
      return;
    }
    b.disabled = true;
    try {
      await aiModelApi({
        method: "POST",
        body: JSON.stringify({
          pricingId: current.pricingId,
          creditCost: Number($("[data-ai-model-cost]", row).value),
          active: $("[data-ai-model-active]", row).checked,
          sortOrder: Number(current.sortOrder || 0),
          reason,
          correlationId: uuid(),
        }),
      });
      toast("Đã lưu giá model AI.");
      await loadAiModels();
    } catch (error) {
      toast(error.message, "error");
    } finally {
      b.disabled = false;
    }
  }
  if (b.matches("[data-new-package]")) openPackage();
  if (b.dataset.editPackage) openPackage(b.dataset.editPackage);
  if (b.dataset.editMod) openMod(b.dataset.editMod);
  if (b.matches("[data-new-mod]")) {
    const form = $("[data-mod-upload-form]");
    form.reset();
    form.gameId.value = "audition";
    form.gameDisplayName.value = "Audition";
    form.compatibilityInformation.value = "Tương thích với audition-vn";
    updateModFilePicker(null);
    updateTemplateIdPreview("");
    $("[data-mod-upload-dialog]").showModal();
  }
  if (b.matches("[data-new-gift]")) {
    $("[data-gift-form]").reset();
    $("[data-gift-dialog]").showModal();
  }
  if (b.matches("[data-logout]")) {
    try {
      await api("/v1/admin/session/logout", { method: "POST", body: "{}" });
    } finally {
      showLogin();
    }
  }
  if (b.dataset.deviceAction) {
    const reason = await confirmAction(
      "Thay đổi trạng thái thiết bị",
      `Bạn sắp ${b.dataset.deviceAction} thiết bị này.`,
    );
    if (reason) {
      await api(`/v1/admin/devices/${b.dataset.id}/${b.dataset.deviceAction}`, {
        method: "POST",
        headers: { "X-Admin-Reason": reason, "X-Admin-Correlation-Id": uuid() },
        body: "{}",
      });
      toast("Đã cập nhật thiết bị.");
      loadDevices();
    }
  }
  if (b.dataset.revokeGift) {
    const reason = await confirmAction(
      "Thu hồi gift code",
      "Mã sẽ ngừng hoạt động ngay lập tức.",
    );
    if (reason) {
      await api(`/v1/admin/gift-codes/${b.dataset.revokeGift}/revoke`, {
        method: "POST",
        body: JSON.stringify({ reason, correlationId: uuid() }),
      });
      toast("Đã thu hồi gift code.");
      loadGifts();
    }
  }
  if (b.dataset.archivePackage) {
    const reason = await confirmAction(
      "Archive gói nạp",
      "Gói sẽ không còn được phát hành nhưng lịch sử giao dịch được giữ nguyên.",
    );
    if (reason) {
      await api(`/v1/admin/packages/${b.dataset.archivePackage}/archive`, {
        method: "POST",
        body: JSON.stringify({ reason, correlationId: uuid() }),
      });
      toast("Đã archive gói nạp.");
      loadPackages();
    }
  }
  if (b.dataset.toggleAdmin) {
    const a = state.admins.find((x) => x.adminUserId === b.dataset.toggleAdmin),
      reason = await confirmAction(
        "Thay đổi quyền admin",
        a?.isActive
          ? "Tài khoản sẽ mất quyền truy cập."
          : "Tài khoản sẽ được kích hoạt.",
      );
    if (reason && a) {
      await api(`/v1/admin/admins/${a.adminUserId}`, {
        method: "PATCH",
        body: JSON.stringify({
          role: a.role,
          isActive: !a.isActive,
          mfaState: a.mfaState,
          displayLabel: a.displayLabel,
          reason,
          correlationId: uuid(),
        }),
      });
      toast("Đã cập nhật tài khoản admin.");
      loadAdmins();
    }
  }
});
$("[data-login-form]").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = e.currentTarget,
    err = $("[data-login-error]");
  err.hidden = true;
  if (!f.reportValidity()) return;
  f.classList.add("is-loading");
  f.querySelector("button[type=submit]").disabled = true;
  try {
    const session = await api("/v1/admin/session/login", {
      method: "POST",
      body: JSON.stringify({
        email: f.email.value.trim(),
        password: f.password.value,
      }),
    });
    f.password.value = "";
    showApp(session);
  } catch (x) {
    err.textContent = x.message;
    err.hidden = false;
  } finally {
    f.classList.remove("is-loading");
    f.querySelector("button[type=submit]").disabled = false;
  }
});
$("[data-user-form]").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = e.currentTarget;
  if (!f.reportValidity()) return;
  try {
    await api(`/v1/admin/users/${f.userId.value}`, {
      method: "PATCH",
      body: JSON.stringify({
        displayName: f.displayName.value.trim() || null,
        contactEmail: f.contactEmail.value.trim() || null,
        status: f.status.value,
        internalNote: f.internalNote.value.trim() || null,
        reason: f.reason.value.trim(),
        correlationId: uuid(),
      }),
    });
    $("[data-user-dialog]").close();
    toast("Đã cập nhật người dùng.");
    loadUsers();
  } catch (x) {
    toast(x.message, "error");
  }
});
$("[data-package-form]").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = e.currentTarget;
  if (!f.reportValidity()) return;
  const isCredits = f.kind.value === "credits";
  try {
    await api("/v1/admin/packages", {
      method: "POST",
      body: JSON.stringify({
        packageId: f.packageId.value || null,
        productId: f.productId.value.trim(),
        displayName: f.displayName.value.trim(),
        description: f.description.value.trim() || null,
        amountMinor: Number(f.amountMinor.value),
        currency: f.currency.value.trim(),
        credits: isCredits ? Number(f.credits.value) : null,
        durationDays: isCredits ? null : Number(f.durationDays.value),
        isActive: f.isActive.checked,
        sortOrder: Number(f.sortOrder.value),
        reason: f.reason.value.trim(),
        correlationId: uuid(),
      }),
    });
    $("[data-package-dialog]").close();
    toast("Đã lưu gói nạp.");
    loadPackages();
  } catch (x) {
    toast(x.message, "error");
  }
});
$("[data-mod-form]").addEventListener("submit", async (e) => {
  e.preventDefault();
  const form = e.currentTarget;
  if (!form.reportValidity()) return;
  try {
    await modApi({
      action: "update",
      gameId: form.gameId.value,
      modId: form.modId.value,
      displayName: form.displayName.value.trim(),
      category: "archive",
      description: form.description.value.trim(),
      compatibilityInformation: form.compatibilityInformation.value.trim(),
      isActive: form.isActive.checked,
    });
    $("[data-mod-dialog]").close();
    toast("Đã cập nhật catalog Mod.");
    await loadMods();
  } catch (error) {
    toast(error.message, "error");
  }
});
async function sha256Hex(file) {
  const digest = await crypto.subtle.digest(
    "SHA-256",
    await file.arrayBuffer(),
  );
  return [...new Uint8Array(digest)]
    .map((value) => value.toString(16).padStart(2, "0"))
    .join("")
    .toUpperCase();
}
function formatFileSize(bytes) {
  if (!Number.isFinite(bytes) || bytes < 1024) return `${number(bytes)} byte`;
  const units = ["KB", "MB", "GB"];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${new Intl.NumberFormat("vi-VN", { maximumFractionDigits: 1 }).format(value)} ${units[unit]}`;
}
function updateModFilePicker(file) {
  const picker = $("[data-mod-file-picker]");
  const title = $("[data-mod-file-title]");
  const detail = $("[data-mod-file-detail]");
  picker.classList.toggle("has-file", Boolean(file));
  title.textContent = file ? file.name : "Chọn file Mod";
  detail.textContent = file
    ? `${formatFileSize(file.size)} · Sẵn sàng tải lên kho lưu trữ`
    : "Kéo thả hoặc bấm để chọn archive .ab, .acv";
}
function templateIdFromModId(modId) {
  return String(modId || "")
    .trim()
    .toLowerCase()
    .replace(/_/g, "-")
    .replace(/[^a-z0-9-]/g, "");
}
function updateTemplateIdPreview(modId) {
  $("[data-template-id-preview]").textContent =
    templateIdFromModId(modId) || "—";
}
$("[data-mod-file-input]").addEventListener("change", (event) => {
  updateModFilePicker(event.currentTarget.files?.[0] || null);
});
$("[data-mod-id-input]").addEventListener("input", (event) => {
  updateTemplateIdPreview(event.currentTarget.value);
});
$("[data-mod-upload-form]").addEventListener("submit", async (e) => {
  e.preventDefault();
  const form = e.currentTarget;
  if (!form.reportValidity()) return;
  const archive = form.archive.files?.[0];
  if (!archive || !/\.(ab|acv)$/i.test(archive.name)) {
    toast("Hãy chọn archive .ab hoặc .acv.", "error");
    return;
  }
  const submit = form.querySelector("button[type=submit]");
  submit.disabled = true;
  try {
    const templateId = templateIdFromModId(form.modId.value);
    const templateVersion = "1";
    const prepared = await modApi({
      action: "prepareUpload",
      templateId,
      templateVersion,
      archiveFileName: archive.name,
    });
    const upload = await fetch(prepared.uploadUrl, {
      method: "PUT",
      headers: { "Content-Type": "application/octet-stream" },
      body: archive,
    });
    if (!upload.ok) throw new Error("MOD_PRODUCT_ARCHIVE_UPLOAD_FAILED");
    const sha256 = await sha256Hex(archive);
    await modApi({
      action: "publish",
      gameId: form.gameId.value.trim(),
      gameDisplayName: form.gameDisplayName.value.trim(),
      modId: form.modId.value.trim(),
      displayName: form.displayName.value.trim(),
      category: "archive",
      description: form.description.value.trim(),
      compatibilityInformation: form.compatibilityInformation.value.trim(),
      templateId,
      templateVersion,
      archiveFileName: archive.name,
      contentLength: archive.size,
      sha256,
      engineType: "acv_tool_5",
      regionProfileId: "audition_vn",
      expectedExtractFolderName: templateId,
      slots: [],
    });
    $("[data-mod-upload-dialog]").close();
    toast("Đã tải lên và phát hành archive Mod.");
    await loadMods();
  } catch (error) {
    toast(error.message, "error");
  } finally {
    submit.disabled = false;
  }
});
$("[data-gift-form]").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = e.currentTarget;
  if (!f.reportValidity()) return;
  const credits = Number(f.creditAmount.value) || 0,
    days = Number(f.durationDays.value) || 0;
  if (credits <= 0 && days <= 0) {
    toast("Phải nhập ít nhất 1 trong 2: credits hoặc số ngày", "error");
    return;
  }
  try {
    const result = await api("/v1/admin/gift-codes", {
      method: "POST",
      body: JSON.stringify({
        durationDays: days || null,
        creditAmount: credits || null,
        maximumRedemptions: Number(f.maximumRedemptions.value),
        expiresAt: f.expiresAt.value
          ? new Date(f.expiresAt.value).toISOString()
          : null,
        reason: f.reason.value.trim(),
        correlationId: uuid(),
      }),
    });
    $("[data-gift-dialog]").close();
    if (result.oneTimeCode) {
      await navigator.clipboard?.writeText(result.oneTimeCode);
      toast(`Đã tạo và sao chép mã: ${result.oneTimeCode}`);
    } else toast("Đã tạo gift code.");
    loadGifts();
  } catch (x) {
    toast(x.message, "error");
  }
});
// Gift kind selector removed - now supports hybrid (both fields visible)
$("[data-package-kind]").addEventListener("change", (e) => {
  const isCredits = e.target.value === "credits";
  $("[data-credits-field]").hidden = !isCredits;
  $("[data-days-field]").hidden = isCredits;
  $("[data-package-form] [name=credits]").required = isCredits;
  $("[data-package-form] [name=durationDays]").required = !isCredits;
});
$$("[data-range] button").forEach((b) =>
  b.addEventListener("click", () => {
    $$("[data-range] button").forEach((x) => x.classList.remove("is-active"));
    b.classList.add("is-active");
    state.days = Number(b.dataset.days);
    loadAnalytics().catch((x) => showError(x.message));
  }),
);
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") $("[data-sidebar]").classList.remove("is-open");
  if (e.key === "Enter" && e.target.matches("[data-user-search]")) loadUsers();
  if (e.key === "Enter" && e.target.matches("[data-device-search]"))
    loadDevices();
  if (e.key === "Enter" && e.target.matches("[data-transaction-search]"))
    loadTransactions();
});
$("[data-api-status]").previousElementSibling.textContent = "Supabase";
window.AdminPortal = {
  state,
  views,
  $,
  ensureConfig,
  date,
  applyRoleVisibility,
  toast,
  showError,
  loadView,
};
installCommerceTabs();
restoreSession();
document.addEventListener("click", async (event) => {
  const button = event.target.closest("button");
  if (!button) return;
  try {
    if (button.matches("[data-payment-submit]")) await loadPayments();
    if (button.dataset.paymentDetail)
      await showPaymentDetail(button.dataset.paymentDetail);
    if (button.matches("[data-payment-retry]")) {
      const reason = await confirmAction(
        "Retry fulfillment",
        "Chỉ tiếp tục đơn đã nhận tiền nhưng chưa fulfillment. Thao tác được ghi audit.",
      );
      if (reason) {
        await paymentApi(`orders/${button.dataset.orderId}/retry`, {
          method: "POST",
          body: JSON.stringify({ reason, correlation_id: uuid() }),
        });
        $("[data-payment-detail-dialog]").close();
        toast("Đã retry fulfillment.");
        await loadPayments();
      }
    }
    if (button.matches("[data-new-payment-product]")) {
      const form = $("[data-payment-product-form]");
      form.reset();
      form.sortOrder.value = "0";
      form.active.checked = true;
      $("[data-payment-product-dialog]").showModal();
    }
    if (button.matches("[data-payment-product-close]"))
      $("[data-payment-product-dialog]").close();
    if (button.matches("[data-payment-detail-close]"))
      $("[data-payment-detail-dialog]").close();
  } catch (error) {
    showError(error.message);
  }
});
$("[data-payment-product-form]").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = event.currentTarget;
  if (!form.reportValidity()) return;
  const subscription = form.productType.value === "subscription";
  try {
    await paymentApi("products", {
      method: "POST",
      body: JSON.stringify({
        productId: form.productId.value.trim(),
        displayName: form.displayName.value.trim(),
        productType: form.productType.value,
        priceVnd: Number(form.priceVnd.value),
        durationDays: subscription ? Number(form.durationDays.value) : null,
        creditAmount: subscription ? null : Number(form.creditAmount.value),
        sortOrder: Number(form.sortOrder.value),
        active: form.active.checked,
        reason: form.reason.value.trim(),
        correlationId: uuid(),
      }),
    });
    $("[data-payment-product-dialog]").close();
    toast("Đã tạo version sản phẩm mới.");
    await loadPayments();
  } catch (error) {
    toast(error.message, "error");
  }
});
