"use strict";

// This page accepts a replacement JSON value only. Existing credentials are never returned.
(() => {
  const portal = window.AdminPortal;
  if (!portal) return;
  const { views, state, $, ensureConfig, date, applyRoleVisibility, toast, showError } = portal;
  views.aiProvider = ["AI PROVIDER", "Vertex AI"];
  const pane = document.createElement("section");
  pane.className = "view";
  pane.dataset.pane = "aiProvider";
  pane.innerHTML = `<div class="section-heading"><div><p class="eyebrow">TÍCH HỢP AI</p><h2>Vertex AI</h2><p>Gemini tổng hợp prompt, TST tạo ảnh. Dán JSON service account; hệ thống tự lấy project và dùng model Gemini mới nhất.</p></div></div><article class="panel"><div class="panel-head"><div><p class="eyebrow">TRẠNG THÁI CREDENTIAL</p><h3 data-vertex-status>Đang tải...</h3></div><button class="secondary-button" data-vertex-refresh>Làm mới</button></div><dl class="detail-list"><div><dt>Project</dt><dd data-vertex-project>--</dd></div><div><dt>Vùng</dt><dd data-vertex-region>--</dd></div><div><dt>Model tự động</dt><dd data-vertex-model>Gemini 3.6 / 3.1</dd></div><div><dt>Cập nhật lúc</dt><dd data-vertex-updated>--</dd></div></dl></article><article class="panel owner-only"><div class="panel-head"><div><p class="eyebrow">XOAY VÒNG CREDENTIAL</p><h3>Cập nhật service account</h3></div></div><form data-vertex-form><div class="form-grid"><label class="span-2">JSON service account<textarea name="credentialsJson" rows="10" required minlength="200" maxlength="20000" spellcheck="false" autocomplete="off" placeholder="Dán nội dung JSON service account tại đây..."></textarea><small class="field-hint">Key chỉ được gửi một lần qua kết nối bảo mật và lưu trong Vault. Không lưu trong trình duyệt.</small></label></div><div class="modal-actions"><button class="primary-button" type="submit">Lưu và kiểm tra key</button></div></form></article>`;
  document.querySelector("main.content").append(pane);
  const nav = document.createElement("button");
  // The RPC remains owner-only; keep the route discoverable so the role-aware
  // pane can show the correct access state after the session is loaded.
  nav.className = "nav-item";
  nav.dataset.view = "aiProvider";
  nav.innerHTML = "<i>AI</i>Vertex AI";
  document.querySelector(".main-nav").append(nav);

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

  async function loadProvider() {
    const value = await providerRpc("status");
    $("[data-vertex-status]").textContent = value.configured ? "Đã cấu hình." : "Chưa có credential Vertex AI.";
    $("[data-vertex-project]").textContent = value.projectId || "--";
    $("[data-vertex-region]").textContent = value.region || "--";
    $("[data-vertex-model]").textContent = value.modelId || "Gemini 3.6 / 3.1";
    $("[data-vertex-updated]").textContent = date(value.updatedAt);
    applyRoleVisibility();
  }

  document.addEventListener("click", async (event) => {
    const button = event.target.closest("button");
    if (button?.dataset.view === "aiProvider" || button?.matches("[data-vertex-refresh]")) {
      try { await loadProvider(); } catch (error) { showError(error.message); }
    }
  });
  window.$("[data-vertex-form]").addEventListener("submit", async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    const credentialsJson = form.credentialsJson.value.trim();
    if (credentialsJson.length < 200 || credentialsJson.length > 20_000) return toast("JSON key không hợp lệ.", "error");
    try {
      const parsed = JSON.parse(credentialsJson);
      if (parsed?.type !== "service_account" || typeof parsed?.project_id !== "string") throw new Error("VERTEX_CREDENTIALS_INVALID");
      await providerRpc("save_vertex_credentials", { credentialsJson, correlationId: crypto.randomUUID() });
      form.reset();
      toast("Đã lưu key Vertex trong Vault.");
      await loadProvider();
    } catch {
      toast("Không thể lưu key Vertex. Kiểm tra JSON và quyền owner.", "error");
    }
  });
})();
