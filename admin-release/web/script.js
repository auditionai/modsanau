document.documentElement.classList.add("js");

const storageKeys = {
  apiBase: "aams.admin.apiBase",
  token: "aams.admin.token",
  adminEmail: "aams.admin.adminEmail",
  displayName: "aams.admin.displayName",
  bootstrapUserId: "aams.admin.bootstrapUserId",
  pageSize: "aams.admin.pageSize",
};

const state = {
  apiBase: "",
  token: "",
  adminEmail: "",
  displayName: "",
  bootstrapUserId: "",
  pageSize: 25,
  activeController: null,
  bootstrap: null,
  dashboard: null,
  devices: null,
  giftCodes: null,
  audit: null,
  selectedDeviceId: null,
  selectedGiftCodeId: null,
  selectedGiftCode: null,
};

const elements = {
  connectionPill: document.querySelector("[data-connection-pill]"),
  cancelButton: document.querySelector("[data-cancel-request]"),
  toast: document.querySelector("[data-toast]"),
  apiBase: document.querySelector("[data-api-base]"),
  authToken: document.querySelector("[data-auth-token]"),
  adminEmail: document.querySelector("[data-admin-email]"),
  displayName: document.querySelector("[data-display-name]"),
  bootstrapUserId: document.querySelector("[data-bootstrap-user-id]"),
  pageSize: document.querySelector("[data-page-size]"),
  bootstrapSummary: document.querySelector("[data-bootstrap-summary]"),
  deviceSummary: document.querySelector("[data-device-summary]"),
  giftSummary: document.querySelector("[data-gift-summary]"),
  auditSummary: document.querySelector("[data-audit-summary]"),
  dashboardUpdated: document.querySelector("[data-dashboard-updated]"),
  bootstrapState: document.querySelector("[data-bootstrap-state]"),
  bootstrapConfigured: document.querySelector("[data-bootstrap-configured]"),
  bootstrapBootstrapped: document.querySelector("[data-bootstrap-bootstrapped]"),
  bootstrapCan: document.querySelector("[data-bootstrap-can]"),
  dashboardGrid: document.querySelector("[data-dashboard-grid]"),
  deviceTable: document.querySelector("[data-device-table]"),
  deviceDetailEmpty: document.querySelector("[data-device-detail-empty]"),
  deviceDetail: document.querySelector("[data-device-detail]"),
  deviceDetailState: document.querySelector("[data-device-detail-state]"),
  deviceCode: document.querySelector("[data-device-code]"),
  deviceUserId: document.querySelector("[data-device-user-id]"),
  deviceSubscription: document.querySelector("[data-device-subscription]"),
  deviceCredits: document.querySelector("[data-device-credits]"),
  deviceActivity: document.querySelector("[data-device-activity]"),
  deviceRedemptions: document.querySelector("[data-device-redemptions]"),
  giftCodeList: document.querySelector("[data-gift-code-list]"),
  giftDetailEmpty: document.querySelector("[data-gift-detail-empty]"),
  giftDetail: document.querySelector("[data-gift-detail]"),
  giftDetailState: document.querySelector("[data-gift-detail-state]"),
  giftPrefix: document.querySelector("[data-gift-prefix]"),
  giftKind: document.querySelector("[data-gift-kind]"),
  giftExpires: document.querySelector("[data-gift-expires]"),
  giftRedemptions: document.querySelector("[data-gift-redemptions]"),
  auditList: document.querySelector("[data-audit-list]"),
  confirmDialog: document.querySelector("[data-confirm-dialog]"),
  confirmTitle: document.querySelector("[data-confirm-title]"),
  confirmMessage: document.querySelector("[data-confirm-message]"),
  confirmCancel: document.querySelector("[data-confirm-cancel]"),
  confirmCommit: document.querySelector("[data-confirm-commit]"),
};

function readStorage(key, fallback = "") {
  return window.localStorage.getItem(key) ?? fallback;
}

function writeStorage(key, value) {
  if (value) window.localStorage.setItem(key, value);
  else window.localStorage.removeItem(key);
}

function hydrateConfig() {
  state.apiBase = normalizeBase(readStorage(storageKeys.apiBase, window.location.origin));
  state.token = readStorage(storageKeys.token);
  state.adminEmail = readStorage(storageKeys.adminEmail);
  state.displayName = readStorage(storageKeys.displayName);
  state.bootstrapUserId = readStorage(storageKeys.bootstrapUserId);
  state.pageSize = clampInt(readStorage(storageKeys.pageSize, "25"), 25, 5, 100);

  if (elements.apiBase) elements.apiBase.value = state.apiBase;
  if (elements.authToken) elements.authToken.value = state.token;
  if (elements.adminEmail) elements.adminEmail.value = state.adminEmail;
  if (elements.displayName) elements.displayName.value = state.displayName;
  if (elements.bootstrapUserId) elements.bootstrapUserId.value = state.bootstrapUserId;
  if (elements.pageSize) elements.pageSize.value = String(state.pageSize);
}

function normalizeBase(value) {
  const trimmed = String(value ?? "").trim();
  if (!trimmed) return window.location.origin;
  return trimmed.replace(/\/+$/, "");
}

function clampInt(value, fallback, minimum, maximum) {
  const parsed = Number.parseInt(String(value), 10);
  if (Number.isNaN(parsed)) return fallback;
  return Math.min(maximum, Math.max(minimum, parsed));
}

function setToast(message, tone = "info") {
  if (!elements.toast) return;
  elements.toast.dataset.tone = tone;
  elements.toast.textContent = message;
}

function setConnectionState(connected, label) {
  if (!elements.connectionPill) return;
  elements.connectionPill.textContent = label;
  elements.connectionPill.dataset.state = connected ? "connected" : "idle";
}

function setBusy(busy) {
  document.documentElement.toggleAttribute("data-busy", busy);
  if (elements.cancelButton) elements.cancelButton.hidden = !busy;
}

function beginRequest() {
  if (state.activeController) state.activeController.abort();
  state.activeController = new AbortController();
  setBusy(true);
  return state.activeController;
}

function finishRequest() {
  state.activeController = null;
  setBusy(false);
}

async function apiFetch(path, options = {}) {
  const controller = state.activeController ?? beginRequest();
  const headers = new Headers(options.headers ?? {});
  headers.set("Accept", "application/json");
  if (state.token) headers.set("Authorization", `Bearer ${state.token}`);
  if (options.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");

  const response = await fetch(`${state.apiBase}${path}`, {
    ...options,
    headers,
    signal: options.signal ?? controller.signal,
  });

  const text = await response.text();
  const data = text ? safeParseJson(text) : null;
  if (!response.ok) {
    const detail = data?.title || data?.code || data?.detail || response.statusText || "Request failed";
    throw new Error(detail);
  }
  return data;
}

function safeParseJson(value) {
  try {
    return JSON.parse(value);
  } catch {
    return null;
  }
}

function formatDateTime(value) {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return String(value);
  return new Intl.DateTimeFormat("vi-VN", {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(date);
}

function formatNumber(value) {
  return new Intl.NumberFormat("vi-VN").format(Number(value ?? 0));
}

function setMetricText(selector, value) {
  const el = document.querySelector(selector);
  if (el) el.textContent = value;
}

function clearList(list) {
  if (list) list.replaceChildren();
}

function renderBootstrap(snapshot) {
  state.bootstrap = snapshot;
  if (!snapshot) {
    if (elements.bootstrapState) elements.bootstrapState.textContent = "Chưa tải";
    return;
  }

  const admin = snapshot.admin;
  const summaryText = snapshot.bootstrapped
    ? admin
      ? `${admin.displayLabel ?? admin.adminUserId} · ${admin.role}`
      : "Đã bootstrap"
    : snapshot.bootstrapConfigured
      ? "Sẵn sàng bootstrap"
      : "Chưa cấu hình";

  if (elements.bootstrapState) {
    elements.bootstrapState.textContent = `${snapshot.diagnosticCode} · ${summaryText}`;
  }
  if (elements.bootstrapConfigured) elements.bootstrapConfigured.textContent = yesNo(snapshot.bootstrapConfigured);
  if (elements.bootstrapBootstrapped) elements.bootstrapBootstrapped.textContent = yesNo(snapshot.bootstrapped);
  if (elements.bootstrapCan) elements.bootstrapCan.textContent = yesNo(snapshot.bootstrapConfigured && !snapshot.admin);
  setMetricText("[data-bootstrap-summary]", summaryText);
}

function yesNo(value) {
  return value ? "Có" : "Không";
}

function renderDashboard(snapshot) {
  state.dashboard = snapshot;
  if (!snapshot) return;

  const summary = snapshot.summary ?? {};
  const cards = elements.dashboardGrid?.querySelectorAll(".dashboard-stat");
  const values = [
    summary.totalDevices,
    summary.activeDevices,
    summary.blockedDevices,
    summary.revokedDevices,
    summary.availableCredits,
    summary.activeGiftCodes,
  ];

  cards?.forEach((card, index) => {
    const value = card.querySelector("strong");
    if (value) value.textContent = formatNumber(values[index] ?? 0);
  });

  if (elements.dashboardUpdated) {
    elements.dashboardUpdated.textContent = `Cập nhật: ${formatDateTime(summary.observedAt)}`;
  }
  setMetricText("[data-device-summary]", formatNumber(summary.totalDevices ?? 0));
  setMetricText("[data-gift-summary]", formatNumber(summary.activeGiftCodes ?? 0));
  setMetricText("[data-audit-summary]", formatNumber(summary.adminActions ?? 0));
}

function renderDevices(payload) {
  state.devices = payload;
  if (!elements.deviceTable) return;

  const items = payload?.items ?? [];
  clearList(elements.deviceTable);
  if (!items.length) {
    const row = document.createElement("tr");
    row.innerHTML = `<td colspan="5" class="empty-state">Không có thiết bị phù hợp.</td>`;
    elements.deviceTable.append(row);
    return;
  }

  for (const item of items) {
    const row = document.createElement("tr");
    row.innerHTML = `
      <td>
        <button class="row-button" type="button" data-select-device="${item.deviceProfileId}">
          <strong>${escapeHtml(item.publicDeviceCode)}</strong>
          <span>${escapeHtml(item.deviceProfileId)}</span>
        </button>
      </td>
      <td><span class="status-chip status-${escapeHtml(item.deviceStatus)}">${escapeHtml(item.deviceStatus)}</span></td>
      <td>${escapeHtml(item.subscriptionStatus)}</td>
      <td>${formatNumber(item.availableCredits)} / ${formatNumber(item.reservedCredits)}</td>
      <td>${formatDateTime(item.lastSeenAt)}</td>
    `;
    elements.deviceTable.append(row);
  }
}

function renderDeviceDetail(detail) {
  if (!elements.deviceDetail || !elements.deviceDetailEmpty || !elements.deviceDetailState) return;
  if (!detail) {
    elements.deviceDetail.hidden = true;
    elements.deviceDetailEmpty.hidden = false;
    elements.deviceDetailState.textContent = "Chưa chọn";
    return;
  }

  state.selectedDeviceId = detail.device.deviceProfileId;
  elements.deviceDetail.hidden = false;
  elements.deviceDetailEmpty.hidden = true;
  elements.deviceDetailState.textContent = detail.device.deviceStatus;
  if (elements.deviceCode) elements.deviceCode.textContent = detail.device.publicDeviceCode;
  if (elements.deviceUserId) elements.deviceUserId.textContent = detail.device.authUserId;
  if (elements.deviceSubscription) {
    elements.deviceSubscription.textContent = `${detail.device.subscriptionStatus} · ${formatDateTime(detail.device.subscriptionExpiresAt)}`;
  }
  if (elements.deviceCredits) {
    elements.deviceCredits.textContent = `${formatNumber(detail.device.availableCredits)} / ${formatNumber(detail.device.reservedCredits)}`;
  }
  renderTimeline(elements.deviceActivity, detail.recentEvents ?? [], (item) =>
    `${item.eventType} · ${formatDateTime(item.eventAt)} · ${item.publicDeviceCode} · ${item.metadata}`
  );
  renderTimeline(elements.deviceRedemptions, detail.redemptions ?? [], (item) =>
    `${formatDateTime(item.redeemedAt)} · ${item.publicDeviceCode} · ${item.durationDays ?? "—"} ngày · ${formatNumber(item.creditAmount)} credits`
  );
}

function renderGiftCodes(payload) {
  state.giftCodes = payload;
  if (!elements.giftCodeList) return;

  clearList(elements.giftCodeList);
  const items = payload?.items ?? [];
  if (!items.length) {
    const row = document.createElement("tr");
    row.innerHTML = `<td colspan="5" class="empty-state">Chưa có gift code nào.</td>`;
    elements.giftCodeList.append(row);
    return;
  }

  for (const item of items) {
    const row = document.createElement("tr");
    row.innerHTML = `
      <td>
        <button class="row-button" type="button" data-select-gift="${item.giftCodeId}">
          <strong>${escapeHtml(item.codePrefix)}</strong>
          <span>${escapeHtml(item.giftCodeId)}</span>
        </button>
      </td>
      <td>${escapeHtml(item.kind)}</td>
      <td>${giftDetailLabel(item)}</td>
      <td>${formatNumber(item.redemptionCount)} / ${formatNumber(item.maximumRedemptions)}</td>
      <td><span class="status-chip ${item.disabledAt ? "status-blocked" : "status-active"}">${item.disabledAt ? "disabled" : "active"}</span></td>
    `;
    elements.giftCodeList.append(row);
  }
}

function renderGiftDetail(item, redemptions = []) {
  state.selectedGiftCode = item;
  if (!elements.giftDetail || !elements.giftDetailEmpty || !elements.giftDetailState) return;
  if (!item) {
    elements.giftDetail.hidden = true;
    elements.giftDetailEmpty.hidden = false;
    elements.giftDetailState.textContent = "Chưa chọn";
    return;
  }

  elements.giftDetail.hidden = false;
  elements.giftDetailEmpty.hidden = true;
  elements.giftDetailState.textContent = item.disabledAt ? "disabled" : "active";
  if (elements.giftPrefix) elements.giftPrefix.textContent = item.codePrefix;
  if (elements.giftKind) elements.giftKind.textContent = item.kind;
  if (elements.giftExpires) elements.giftExpires.textContent = formatDateTime(item.expiresAt);
  renderTimeline(elements.giftRedemptions, redemptions, (entry) =>
    `${formatDateTime(entry.redeemedAt)} · ${entry.publicDeviceCode} · ${entry.durationDays ?? "—"} ngày · ${formatNumber(entry.creditAmount)} credits`
  );
}

function renderAudit(payload) {
  state.audit = payload;
  renderTimeline(elements.auditList, payload?.items ?? [], (item) =>
    `${item.eventType} · ${item.targetKind} · ${formatDateTime(item.eventAt)} · ${item.details}`
  );
}

function renderTimeline(target, items, mapItem) {
  if (!target) return;
  clearList(target);
  if (!items.length) {
    const li = document.createElement("li");
    li.className = "empty-state";
    li.textContent = "Không có dữ liệu.";
    target.append(li);
    return;
  }

  for (const item of items) {
    const li = document.createElement("li");
    li.textContent = mapItem(item);
    target.append(li);
  }
}

function giftDetailLabel(item) {
  if (item.kind === "credits") return `${formatNumber(item.creditAmount)} credits`;
  if (item.kind === "subscription") return `${item.durationDays ?? "—"} ngày`;
  return "—";
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

async function refreshBootstrap() {
  setToast("Đang kiểm tra bootstrap…");
  const snapshot = await apiFetch("/v1/admin/bootstrap/state");
  renderBootstrap(snapshot);
  setToast("Đã tải bootstrap.");
  setConnectionState(true, "Đã kết nối");
}

async function refreshDashboard() {
  const snapshot = await apiFetch("/v1/admin/dashboard");
  renderDashboard(snapshot);
}

async function refreshDevices() {
  const form = document.querySelector("[data-device-search]");
  const query = form?.querySelector('input[name="query"]')?.value ?? "";
  const status = form?.querySelector('select[name="status"]')?.value ?? "";
  const limit = clampInt(form?.querySelector('input[name="limit"]')?.value ?? String(state.pageSize), state.pageSize, 5, 100);
  const payload = await apiFetch(`/v1/admin/devices?query=${encodeURIComponent(query)}&status=${encodeURIComponent(status)}&limit=${limit}&offset=0`);
  renderDevices(payload);
  if (state.selectedDeviceId) await refreshDeviceDetail(state.selectedDeviceId);
}

async function refreshGiftCodes() {
  const payload = await apiFetch(`/v1/admin/gift-codes?limit=${state.pageSize}&offset=0`);
  renderGiftCodes(payload);
  if (state.selectedGiftCodeId) await refreshGiftDetail(state.selectedGiftCodeId);
}

async function refreshAudit() {
  const payload = await apiFetch(`/v1/admin/audit?limit=${state.pageSize}&offset=0`);
  renderAudit(payload);
}

async function refreshDeviceDetail(deviceId) {
  const payload = await apiFetch(`/v1/admin/devices/${deviceId}?activityLimit=10&redemptionLimit=10`);
  renderDeviceDetail(payload);
}

async function refreshGiftDetail(giftCodeId) {
  const items = await apiFetch(`/v1/admin/gift-codes/${giftCodeId}/redemptions?limit=20`);
  renderGiftDetail(state.selectedGiftCode, items?.items ?? []);
}

async function refreshAll() {
  try {
    beginRequest();
    setToast("Đang tải dashboard…");
    await Promise.all([
      refreshBootstrap(),
      refreshDashboard(),
      refreshDevices(),
      refreshGiftCodes(),
      refreshAudit(),
    ]);
    setToast("Đã đồng bộ dashboard.");
  } catch (error) {
    reportError(error);
  } finally {
    finishRequest();
  }
}

function reportError(error) {
  if (error?.name === "AbortError") {
    setToast("Đã hủy request hiện tại.", "warning");
    return;
  }
  const message = error instanceof Error ? error.message : "Request thất bại";
  setToast(message, "error");
  setConnectionState(false, "Lỗi kết nối");
}

async function submitBootstrap(form) {
  const payload = {
    email: form.elements.email.value.trim() || null,
    displayName: form.elements.displayName.value.trim() || null,
  };
  const result = await apiFetch("/v1/admin/bootstrap", {
    method: "POST",
    body: JSON.stringify(payload),
  });
  renderBootstrap(result);
  setToast("Đã bootstrap admin.");
}

async function mutateDevice(deviceId, status) {
  const labels = { blocked: "Block thiết bị", active: "Unblock thiết bị", revoked: "Revoke thiết bị" };
  const routes = { blocked: "block", active: "unblock", revoked: "revoke" };
  const confirmed = await confirmDanger(labels[status] ?? "Xác nhận", `Bạn đang đổi trạng thái thiết bị ${deviceId} sang ${status}.`, "Tiếp tục");
  if (!confirmed) return;

  try {
    beginRequest();
    await apiFetch(`/v1/admin/devices/${deviceId}/${routes[status]}`, {
      method: "POST",
      body: JSON.stringify({
        reason: "Admin portal action",
        correlationId: crypto.randomUUID(),
      }),
    });
    setToast(`Đã ${status} thiết bị.`);
    await Promise.all([refreshDevices(), refreshDashboard(), refreshAudit(), refreshDeviceDetail(deviceId)]);
  } finally {
    finishRequest();
  }
}

async function extendSubscription(deviceId, form) {
  await apiFetch(`/v1/admin/devices/${deviceId}/subscription/extend`, {
    method: "POST",
    body: JSON.stringify({
      extensionDays: clampInt(form.elements.extensionDays.value, 30, 1, 3650),
      reason: form.elements.reason.value.trim() || "Admin portal action",
      correlationId: crypto.randomUUID(),
    }),
  });
  setToast("Đã gia hạn subscription.");
  await Promise.all([refreshDevices(), refreshDashboard(), refreshAudit(), refreshDeviceDetail(deviceId)]);
}

async function grantCredits(form) {
  const targetUserId = form.elements.targetUserId.value.trim();
  if (!targetUserId) throw new Error("Thiếu target user id.");
  await apiFetch("/v1/admin/credits/grant", {
    method: "POST",
    body: JSON.stringify({
      targetUserId,
      amount: clampInt(form.elements.amount.value, 100, 1, 1000000000),
      authorityReference: form.elements.authorityReference.value.trim() || "admin-portal",
      reason: form.elements.reason.value.trim() || "Admin portal action",
      correlationId: crypto.randomUUID(),
    }),
  });
  setToast("Đã grant credits.");
  await Promise.all([refreshDashboard(), refreshAudit()]);
}

async function createGiftCode(form) {
  const kind = form.elements.kind.value;
  const durationDays = form.elements.durationDays.value ? clampInt(form.elements.durationDays.value, 30, 1, 3650) : null;
  const creditAmount = form.elements.creditAmount.value ? clampInt(form.elements.creditAmount.value, 100, 1, 1000000000) : null;
  await apiFetch("/v1/admin/gift-codes", {
    method: "POST",
    body: JSON.stringify({
      kind,
      durationDays,
      creditAmount,
      maximumRedemptions: clampInt(form.elements.maximumRedemptions.value, 1, 1, 100000),
      expiresAt: form.elements.expiresAt.value ? new Date(form.elements.expiresAt.value).toISOString() : null,
      reason: form.elements.reason.value.trim() || "admin portal",
      correlationId: crypto.randomUUID(),
    }),
  });
  setToast("Đã tạo gift code.");
  await Promise.all([refreshGiftCodes(), refreshAudit(), refreshDashboard()]);
}

async function revokeGiftCode(giftCodeId) {
  const confirmed = await confirmDanger("Revoke gift code", `Gift code ${giftCodeId} sẽ bị vô hiệu hóa.`, "Revoke");
  if (!confirmed) return;
  try {
    beginRequest();
    await apiFetch(`/v1/admin/gift-codes/${giftCodeId}/revoke`, {
      method: "POST",
      body: JSON.stringify({
        reason: "Admin portal action",
        correlationId: crypto.randomUUID(),
      }),
    });
    setToast("Đã revoke gift code.");
    await Promise.all([refreshGiftCodes(), refreshAudit(), refreshDashboard()]);
  } finally {
    finishRequest();
  }
}

function confirmDanger(title, message, commitLabel) {
  if (!(elements.confirmDialog instanceof HTMLDialogElement)) return Promise.resolve(window.confirm(`${title}\n\n${message}`));
  if (elements.confirmTitle) elements.confirmTitle.textContent = title;
  if (elements.confirmMessage) elements.confirmMessage.textContent = message;
  if (elements.confirmCommit) elements.confirmCommit.textContent = commitLabel;

  return new Promise((resolve) => {
    const dialog = elements.confirmDialog;
    const cleanup = () => {
      dialog.removeEventListener("close", onClose);
      elements.confirmCancel?.removeEventListener("click", onCancel);
      elements.confirmCommit?.removeEventListener("click", onCommit);
    };
    const onClose = () => {
      cleanup();
      resolve(dialog.returnValue === "confirm");
    };
    const onCancel = () => {
      dialog.close("cancel");
    };
    const onCommit = () => {
      dialog.close("confirm");
    };
    dialog.addEventListener("close", onClose, { once: true });
    elements.confirmCancel?.addEventListener("click", onCancel, { once: true });
    elements.confirmCommit?.addEventListener("click", onCommit, { once: true });
    dialog.showModal();
  });
}

function attachEvents() {
  document.querySelectorAll("[data-action='refresh-all']").forEach((button) => {
    button.addEventListener("click", () => refreshAll());
  });
  document.querySelectorAll("[data-action='load-bootstrap']").forEach((button) => {
    button.addEventListener("click", () => runTask(refreshBootstrap));
  });
  document.querySelectorAll("[data-action='refresh-devices']").forEach((button) => {
    button.addEventListener("click", () => runTask(refreshDevices));
  });
  document.querySelectorAll("[data-action='refresh-gift-codes']").forEach((button) => {
    button.addEventListener("click", () => runTask(refreshGiftCodes));
  });
  document.querySelectorAll("[data-action='refresh-audit']").forEach((button) => {
    button.addEventListener("click", () => runTask(refreshAudit));
  });
  elements.cancelButton?.addEventListener("click", () => state.activeController?.abort());

  document.querySelector("[data-config-form]")?.addEventListener("submit", (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    if (!(form instanceof HTMLFormElement)) return;
    state.apiBase = normalizeBase(form.elements.apiBase.value);
    state.token = form.elements.token.value.trim();
    state.adminEmail = form.elements.adminEmail.value.trim();
    state.displayName = form.elements.displayName.value.trim();
    state.bootstrapUserId = form.elements.bootstrapUserId.value.trim();
    state.pageSize = clampInt(form.elements.pageSize.value, 25, 5, 100);
    writeStorage(storageKeys.apiBase, state.apiBase);
    writeStorage(storageKeys.token, state.token);
    writeStorage(storageKeys.adminEmail, state.adminEmail);
    writeStorage(storageKeys.displayName, state.displayName);
    writeStorage(storageKeys.bootstrapUserId, state.bootstrapUserId);
    writeStorage(storageKeys.pageSize, String(state.pageSize));
    setToast("Đã lưu cấu hình.");
    setConnectionState(Boolean(state.token), state.token ? "Sẵn sàng kết nối" : "Chưa có token");
  });

  document.querySelector("[data-bootstrap-form]")?.addEventListener("submit", async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    if (!(form instanceof HTMLFormElement)) return;
    try {
      beginRequest();
      await submitBootstrap(form);
      await refreshAll();
    } catch (error) {
      reportError(error);
    } finally {
      finishRequest();
    }
  });

  document.querySelector("[data-device-search]")?.addEventListener("submit", async (event) => {
    event.preventDefault();
    try {
      beginRequest();
      await refreshDevices();
      setToast("Đã lọc thiết bị.");
    } catch (error) {
      reportError(error);
    } finally {
      finishRequest();
    }
  });

  document.querySelector("[data-gift-form]")?.addEventListener("submit", async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    if (!(form instanceof HTMLFormElement)) return;
    try {
      beginRequest();
      await createGiftCode(form);
    } catch (error) {
      reportError(error);
    } finally {
      finishRequest();
    }
  });

  document.querySelector("[data-credit-form]")?.addEventListener("submit", async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    if (!(form instanceof HTMLFormElement)) return;
    try {
      beginRequest();
      await grantCredits(form);
    } catch (error) {
      reportError(error);
    } finally {
      finishRequest();
    }
  });

  document.querySelector("[data-subscription-form]")?.addEventListener("submit", async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    if (!(form instanceof HTMLFormElement) || !state.selectedDeviceId) return;
    try {
      beginRequest();
      await extendSubscription(state.selectedDeviceId, form);
    } catch (error) {
      reportError(error);
    } finally {
      finishRequest();
    }
  });

  document.querySelector("[data-device-table]")?.addEventListener("click", async (event) => {
    const button = event.target instanceof Element ? event.target.closest("[data-select-device]") : null;
    if (!(button instanceof HTMLElement)) return;
    const deviceId = button.dataset.selectDevice;
    if (!deviceId) return;
    try {
      beginRequest();
      state.selectedDeviceId = deviceId;
      await refreshDeviceDetail(deviceId);
    } catch (error) {
      reportError(error);
    } finally {
      finishRequest();
    }
  });

  document.querySelector("[data-gift-code-list]")?.addEventListener("click", async (event) => {
    const button = event.target instanceof Element ? event.target.closest("[data-select-gift]") : null;
    if (!(button instanceof HTMLElement)) return;
    const giftCodeId = button.dataset.selectGift;
    if (!giftCodeId) return;
    try {
      beginRequest();
      state.selectedGiftCodeId = giftCodeId;
      const item = state.giftCodes?.items?.find((candidate) => candidate.giftCodeId === giftCodeId) ?? null;
      state.selectedGiftCode = item;
      renderGiftDetail(item, []);
      await refreshGiftDetail(giftCodeId);
    } catch (error) {
      reportError(error);
    } finally {
      finishRequest();
    }
  });

  document.querySelector("[data-device-action='blocked']")?.addEventListener("click", () => {
    if (state.selectedDeviceId) mutateDevice(state.selectedDeviceId, "blocked").catch(reportError);
  });
  document.querySelector("[data-device-action='active']")?.addEventListener("click", () => {
    if (state.selectedDeviceId) mutateDevice(state.selectedDeviceId, "active").catch(reportError);
  });
  document.querySelector("[data-device-action='revoked']")?.addEventListener("click", () => {
    if (state.selectedDeviceId) mutateDevice(state.selectedDeviceId, "revoked").catch(reportError);
  });
  document.querySelector("[data-gift-action='revoke']")?.addEventListener("click", () => {
    if (state.selectedGiftCodeId) revokeGiftCode(state.selectedGiftCodeId).catch(reportError);
  });
}

async function runTask(operation) {
  try {
    beginRequest();
    await operation();
  } catch (error) {
    reportError(error);
  } finally {
    finishRequest();
  }
}

function initialize() {
  hydrateConfig();
  attachEvents();
  setConnectionState(Boolean(state.token), state.token ? "Sẵn sàng kết nối" : "Chưa có token");
  setToast(state.token ? "Sẵn sàng đồng bộ." : "Nhập bearer token rồi bấm Làm mới toàn bộ.");
  if (state.token) refreshAll().catch(reportError);
}

initialize();
