"use strict";

// The browser only submits replacement JSON. It never receives stored credentials.
(() => {
  const portal = window.AdminPortal;
  if (!portal) return;
  const { views, state, $, ensureConfig, date, applyRoleVisibility, toast, showError } = portal;
  views.aiProvider = ["AI PROVIDER", "Vertex AI"];

  const styles = document.createElement("style");
  styles.textContent = ".vertex-pool-panel{grid-column:1/-1}.vertex-credential-list{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}.vertex-credential-card{border:1px solid var(--line);border-radius:13px;background:rgba(8,11,28,.48);padding:14px}.vertex-card-head{display:flex;align-items:flex-start;justify-content:space-between;gap:12px}.vertex-card-head strong,.vertex-card-head small{display:block}.vertex-card-head strong{overflow-wrap:anywhere;font-size:14px}.vertex-card-head small{margin-top:4px;color:var(--muted)}.vertex-credential-card dl{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin:16px 0}.vertex-credential-card dt{color:var(--muted);font-size:10px}.vertex-credential-card dd{margin:3px 0 0;color:var(--soft);font-size:11px;overflow-wrap:anywhere}.vertex-card-actions{display:flex;flex-wrap:wrap;gap:7px;border-top:1px solid var(--line);padding-top:12px}.vertex-card-actions button{min-height:34px;padding:0 10px}@media(max-width:1240px){.vertex-credential-list{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:660px){.vertex-credential-list{grid-template-columns:1fr}.vertex-credential-card dl{grid-template-columns:1fr}}";
  document.head.append(styles);

  const pane = document.createElement("section");
  pane.className = "view";
  pane.dataset.pane = "aiProvider";
  pane.innerHTML = `
    <div class="section-heading"><div><p class="eyebrow">TÍCH HỢP AI</p><h2>Pool credential Vertex AI</h2><p>Pool phía server tự động chia tải và đưa credential vào cooldown khi Google báo quota. JSON chỉ lưu trong Vault.</p></div></div>
    <div class="ai-provider-layout">
      <article class="panel credential-hero"><div><p class="eyebrow">TRẠNG THÁI POOL</p><h3><span class="provider-status-dot" data-vertex-dot></span><span data-vertex-status>Đang tải trạng thái...</span></h3></div><button class="secondary-button" type="button" data-vertex-refresh>Làm mới trạng thái</button></article>
      <article class="panel credential-panel"><div class="panel-head"><div><p class="eyebrow">CHÍNH SÁCH XOAY VÒNG</p><h3>Phân bổ thông minh</h3></div></div><dl class="detail-list"><div><dt>Chiến lược</dt><dd>Ít dùng gần đây nhất + tình trạng hoạt động</dd></div><div><dt>Phản hồi quota</dt><dd>Cooldown lũy tiến, tối đa 60 phút</dd></div><div><dt>Chính sách Gemini</dt><dd data-vertex-model>Gemini 3.6 / 3.1</dd></div><div><dt>Dung lượng pool</dt><dd data-vertex-capacity>--</dd></div></dl></article>
      <article class="panel credential-form owner-only"><div class="panel-head"><div><p class="eyebrow">THÊM CREDENTIAL</p><h3>Thêm JSON vào pool</h3></div></div><form data-vertex-form><div class="form-grid"><label>JSON service account<textarea name="credentialsJson" rows="11" required minlength="200" maxlength="20000" spellcheck="false" autocomplete="off" placeholder="Dán toàn bộ JSON service account tại đây..."></textarea><small class="field-hint">Credential được mã hóa và lưu tại Vault. Bạn sẽ không thể xem lại JSON sau khi lưu.</small></label></div><div class="modal-actions"><button class="primary-button" type="submit">Thêm credential</button></div></form></article>
      <article class="panel vertex-pool-panel"><div class="panel-head"><div><p class="eyebrow">DANH SÁCH CREDENTIAL</p><h3>Pool credentials</h3></div><span class="badge" data-vertex-count>0 key</span></div><div class="vertex-credential-list" data-vertex-list></div></article>
    </div>`;
  document.querySelector("main.content").append(pane);

  const nav = document.createElement("button");
  nav.className = "nav-item";
  nav.dataset.view = "aiProvider";
  nav.innerHTML = "<i>AI</i>Vertex AI";
  document.querySelector('[data-view="audit"]')?.insertAdjacentElement("afterend", nav);

  async function providerRpc(action, payload = {}) {
    const config = await ensureConfig();
    const response = await fetch(`${config.supabaseUrl}/rest/v1/rpc/ai_provider_admin_api`, {
      method: "POST",
      headers: { "Content-Type": "application/json", apikey: config.publishableKey, Authorization: `Bearer ${state.accessToken}` },
      body: JSON.stringify({ action, payload }), cache: "no-store",
    });
    const result = await response.json().catch(() => null);
    if (!response.ok) throw new Error(result?.message || result?.code || "VERTEX_ADMIN_FAILED");
    return result;
  }

  function stateLabel(item) {
    if (item.retiredAt) return ["retired", "Đã ngừng"];
    if (!item.enabled) return ["disabled", "Đã tắt"];
    if (item.cooldownUntil && new Date(item.cooldownUntil) > new Date()) return ["pending", "Đang cooldown"];
    return ["active", "Sẵn sàng"];
  }

  function renderPool(items) {
    $("[data-vertex-count]").textContent = `${items.length} key`;
    $("[data-vertex-capacity]").textContent = `${items.filter((item) => item.enabled && !item.retiredAt).length} key đang bật`;
    $("[data-vertex-list]").innerHTML = items.length ? items.map((item) => {
      const [kind, label] = stateLabel(item);
      const action = item.retiredAt ? "" : `<div class="vertex-card-actions owner-only">${item.enabled ? `<button class="ghost-button" data-vertex-disable="${item.credentialId}">Tắt</button>` : `<button class="secondary-button" data-vertex-enable="${item.credentialId}">Bật</button>`}${item.cooldownUntil && item.enabled ? `<button class="secondary-button" data-vertex-reset="${item.credentialId}">Đặt lại cooldown</button>` : ""}<button class="ghost-button" data-vertex-retire="${item.credentialId}">Ngừng sử dụng</button></div>`;
      return `<article class="vertex-credential-card"><div class="vertex-card-head"><div><strong>${item.projectId}</strong><small class="mono">${item.credentialId}</small></div>${badge(kind, label)}</div><dl><div><dt>Lượng dùng gần nhất</dt><dd>${date(item.lastSelectedAt)}</dd></div><div><dt>Lần thành công</dt><dd>${date(item.lastSuccessAt)}</dd></div><div><dt>Cooldown đến</dt><dd>${date(item.cooldownUntil)}</dd></div><div><dt>Lỗi liên tiếp</dt><dd>${Number(item.failureStreak || 0)}</dd></div></dl>${action}</article>`;
    }).join("") : '<div class="empty-state">Chưa có credential trong pool.</div>';
    applyRoleVisibility();
  }

  function badge(kind, label) { return `<span class="badge ${kind}">${label}</span>`; }

  async function loadProvider() {
    const value = await providerRpc("status");
    const items = Array.isArray(value.credentials) ? value.credentials : [];
    const available = items.filter((item) => item.enabled && !item.retiredAt && (!item.cooldownUntil || new Date(item.cooldownUntil) <= new Date()));
    $("[data-vertex-status]").textContent = available.length ? `${available.length} credential sẵn sàng phục vụ` : "Không có credential sẵn sàng";
    $("[data-vertex-dot]").classList.toggle("is-ready", available.length > 0);
    $("[data-vertex-model]").textContent = value.modelPolicy || "Gemini 3.6 / 3.1";
    renderPool(items);
  }

  document.addEventListener("click", async (event) => {
    const button = event.target.closest("button");
    if (!button) return;
    try {
      if (button.dataset.view === "aiProvider" || button.matches("[data-vertex-refresh]")) await loadProvider();
      else if (button.dataset.vertexEnable) { await providerRpc("set_vertex_credential_enabled", { credentialId: button.dataset.vertexEnable, enabled: true, correlationId: crypto.randomUUID() }); await loadProvider(); }
      else if (button.dataset.vertexDisable) { await providerRpc("set_vertex_credential_enabled", { credentialId: button.dataset.vertexDisable, enabled: false, correlationId: crypto.randomUUID() }); await loadProvider(); }
      else if (button.dataset.vertexReset) { await providerRpc("reset_vertex_credential_cooldown", { credentialId: button.dataset.vertexReset, correlationId: crypto.randomUUID() }); await loadProvider(); }
      else if (button.dataset.vertexRetire) { await providerRpc("retire_vertex_credential", { credentialId: button.dataset.vertexRetire, correlationId: crypto.randomUUID() }); await loadProvider(); }
    } catch (error) { showError(error.message); }
  });

  $("[data-vertex-form]").addEventListener("submit", async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    const credentialsJson = form.credentialsJson.value.trim();
    if (credentialsJson.length < 200 || credentialsJson.length > 20_000) return toast("JSON key không hợp lệ.", "error");
    try {
      const parsed = JSON.parse(credentialsJson);
      if (parsed?.type !== "service_account" || typeof parsed?.project_id !== "string") throw new Error("VERTEX_CREDENTIALS_INVALID");
      await providerRpc("add_vertex_credential", { credentialsJson, correlationId: crypto.randomUUID() });
      form.reset();
      toast("Đã thêm credential vào pool.");
      await loadProvider();
    } catch { toast("Không thể thêm credential. Kiểm tra JSON và quyền owner.", "error"); }
  });
  // Server-owned prompt presets for the image-generation workflow.
  views.aiPresets = ["AI STUDIO", "Mẫu tạo ảnh"];
  const presetPane = document.createElement("section");
  presetPane.className = "view";
  presetPane.dataset.pane = "aiPresets";
  presetPane.innerHTML = `
    <div class="section-heading"><div><p class="eyebrow">AI IMAGE PRESETS</p><h2>Mẫu tạo ảnh AI</h2><p>Prompt gốc được giữ trên server; ứng dụng chỉ gửi ID mẫu và dữ liệu sáng tạo của người dùng.</p></div></div>
    <div class="ai-provider-layout"><article class="panel credential-form owner-only"><div class="panel-head"><div><p class="eyebrow">CHỈNH SỬA MẪU</p><h3 data-preset-form-title>Tạo mẫu mới</h3></div><button type="button" class="secondary-button" data-preset-new>Mẫu mới</button></div><form data-ai-preset-form><input name="presetId" type="hidden" /><div class="form-grid"><label>Tên mẫu<input name="name" required maxlength="120" /></label><label>Thứ tự<input name="sortOrder" type="number" min="0" value="0" required /></label><label class="span-2">Prompt gốc<textarea name="basePrompt" rows="12" required maxlength="12000" spellcheck="false"></textarea></label><label class="check-label"><input name="active" type="checkbox" checked /> Hoạt động</label></div><div class="modal-actions"><button type="submit" class="primary-button">Lưu mẫu</button><button type="button" class="ghost-button" data-preset-delete disabled>Xóa mẫu</button></div></form></article><article class="panel vertex-pool-panel"><div class="panel-head"><div><p class="eyebrow">DANH SÁCH</p><h3>Mẫu đang quản lý</h3></div><span class="badge" data-preset-count>0 mẫu</span></div><div class="vertex-credential-list" data-preset-list></div></article></div>`;
  document.querySelector("main.content").append(presetPane);

  const presetNav = document.createElement("button");
  presetNav.className = "nav-item";
  presetNav.dataset.view = "aiPresets";
  presetNav.innerHTML = "<i>AI</i>Mẫu tạo ảnh";
  nav.insertAdjacentElement("afterend", presetNav);

  let presets = [];
  function resetPresetForm() {
    const form = $("[data-ai-preset-form]"); form.reset(); form.presetId.value = ""; form.sortOrder.value = "0"; form.active.checked = true;
    $("[data-preset-form-title]").textContent = "Tạo mẫu mới";
    $("[data-preset-delete]").disabled = true;
  }
  function editPreset(item) {
    const form = $("[data-ai-preset-form]"); form.presetId.value = item.presetId; form.name.value = item.name; form.basePrompt.value = item.basePrompt; form.sortOrder.value = item.sortOrder; form.active.checked = item.active;
    $("[data-preset-form-title]").textContent = `Chỉnh sửa: ${item.name}`;
    $("[data-preset-delete]").disabled = false;
  }
  async function presetApi(action, payload = {}) {
    const config = await ensureConfig();
    const response = await fetch(`${config.supabaseUrl}/functions/v1/ai-model-admin`, { method: "POST", headers: { "Content-Type": "application/json", apikey: config.publishableKey, Authorization: `Bearer ${state.accessToken}` }, body: JSON.stringify({ action, ...payload }), cache: "no-store" });
    const result = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(result.error || "AI_PRESET_REQUEST_FAILED");
    return result;
  }
  function renderPresets() {
    $("[data-preset-count]").textContent = `${presets.length} mẫu`;
    $("[data-preset-list]").innerHTML = presets.length ? presets.map((item) => `<article class="vertex-credential-card"><div class="vertex-card-head"><div><strong>${escape(item.name)}</strong><small>${item.active ? "Hoạt động" : "Đã tắt"} · #${Number(item.sortOrder)}</small></div><button class="secondary-button owner-only" data-preset-edit="${item.presetId}">Chỉnh sửa</button></div></article>`).join("") : '<div class="empty-state">Chưa có mẫu tạo ảnh.</div>';
    applyRoleVisibility();
  }
  function escape(value) { return String(value ?? "").replace(/[&<>'"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[c]); }
  async function loadPresets() { presets = await presetApi("preset_list"); if (!Array.isArray(presets)) presets = []; renderPresets(); }
  document.addEventListener("click", async (event) => {
    const button = event.target.closest("button"); if (!button) return;
    try {
      if (button.dataset.view === "aiPresets") await loadPresets();
      if (button.matches("[data-preset-new]")) resetPresetForm();
      if (button.dataset.presetEdit) { const item = presets.find((value) => value.presetId === button.dataset.presetEdit); if (item) editPreset(item); }
      if (button.matches("[data-preset-delete]")) { const id = $("[data-ai-preset-form]").presetId.value; if (id) { await presetApi("preset_delete", { presetId: id }); resetPresetForm(); await loadPresets(); toast("Đã xóa mẫu tạo ảnh."); } }
    } catch (error) { showError(error.message); }
  });
  $("[data-ai-preset-form]").addEventListener("submit", async (event) => {
    event.preventDefault(); const form = event.currentTarget; if (!form.reportValidity()) return;
    try { await presetApi("preset_save", { presetId: form.presetId.value || null, name: form.name.value.trim(), basePrompt: form.basePrompt.value.trim(), active: form.active.checked, sortOrder: Number(form.sortOrder.value) }); await loadPresets(); toast("Đã lưu mẫu tạo ảnh."); } catch (error) { toast(error.message, "error"); }
  });
})();
