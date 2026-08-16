"use strict";

// The browser accepts a replacement JSON value only. Existing credentials never leave Vault.
(() => {
  const portal = window.AdminPortal;
  if (!portal) return;

  const { views, state, $, ensureConfig, date, applyRoleVisibility, toast, showError } = portal;
  views.aiProvider = ["AI PROVIDER", "Vertex AI"];

  const pane = document.createElement("section");
  pane.className = "view";
  pane.dataset.pane = "aiProvider";
  pane.innerHTML = `
    <div class="section-heading">
      <div>
        <p class="eyebrow">AI ORCHESTRATION</p>
        <h2>Vertex AI control plane</h2>
        <p>Credential cho Gemini duoc luu tai Vault. He thong tu xac dinh project va dung model Gemini duoc phe duyet.</p>
      </div>
    </div>
    <div class="ai-provider-layout">
      <article class="panel credential-hero">
        <div>
          <p class="eyebrow">CREDENTIAL STATUS</p>
          <h3><span class="provider-status-dot" data-vertex-dot></span><span data-vertex-status>Dang tai trang thai...</span></h3>
        </div>
        <button class="secondary-button" type="button" data-vertex-refresh>Lam moi trang thai</button>
      </article>
      <article class="panel credential-panel">
        <div class="panel-head"><div><p class="eyebrow">ACTIVE CONFIGURATION</p><h3>Thong tin ket noi</h3></div></div>
        <dl class="detail-list">
          <div><dt>Google Cloud project</dt><dd data-vertex-project>--</dd></div>
          <div><dt>Serving region</dt><dd data-vertex-region>--</dd></div>
          <div><dt>Gemini policy</dt><dd data-vertex-model>Gemini 3.6 / 3.1</dd></div>
          <div><dt>Cap nhat gan nhat</dt><dd data-vertex-updated>--</dd></div>
        </dl>
      </article>
      <article class="panel credential-form owner-only">
        <div class="panel-head"><div><p class="eyebrow">ROTATE CREDENTIAL</p><h3>Thay the service account</h3></div></div>
        <form data-vertex-form>
          <div class="form-grid">
            <label>JSON service account
              <textarea name="credentialsJson" rows="11" required minlength="200" maxlength="20000" spellcheck="false" autocomplete="off" placeholder="Dan toan bo noi dung JSON service account tai day..."></textarea>
              <small class="field-hint">Chi duoc gui mot lan qua ket noi bao mat. Trinh duyet khong luu credential nay.</small>
            </label>
          </div>
          <div class="modal-actions"><button class="primary-button" type="submit">Luu va kiem tra credential</button></div>
        </form>
      </article>
    </div>`;
  document.querySelector("main.content").append(pane);

  const nav = document.createElement("button");
  nav.className = "nav-item";
  nav.dataset.view = "aiProvider";
  nav.innerHTML = "<i>AI</i>Vertex AI";
  const auditNav = document.querySelector('[data-view="audit"]');
  auditNav?.insertAdjacentElement("afterend", nav);

  async function providerRpc(action, payload = {}) {
    const config = await ensureConfig();
    const response = await fetch(`${config.supabaseUrl}/rest/v1/rpc/ai_provider_admin_api`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        apikey: config.publishableKey,
        Authorization: `Bearer ${state.accessToken}`,
      },
      body: JSON.stringify({ action, payload }),
      cache: "no-store",
    });
    const result = await response.json().catch(() => null);
    if (!response.ok) throw new Error(result?.message || result?.code || "VERTEX_ADMIN_FAILED");
    return result;
  }

  async function loadProvider() {
    const value = await providerRpc("status");
    const configured = Boolean(value.configured);
    $("[data-vertex-status]").textContent = configured ? "Credential dang hoat dong" : "Chua co credential Vertex AI";
    $("[data-vertex-dot]").classList.toggle("is-ready", configured);
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

  $("[data-vertex-form]").addEventListener("submit", async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    const credentialsJson = form.credentialsJson.value.trim();
    if (credentialsJson.length < 200 || credentialsJson.length > 20_000) {
      toast("JSON key khong hop le.", "error");
      return;
    }
    try {
      const parsed = JSON.parse(credentialsJson);
      if (parsed?.type !== "service_account" || typeof parsed?.project_id !== "string") {
        throw new Error("VERTEX_CREDENTIALS_INVALID");
      }
      await providerRpc("save_vertex_credentials", { credentialsJson, correlationId: crypto.randomUUID() });
      form.reset();
      toast("Da luu credential Vertex trong Vault.");
      await loadProvider();
    } catch {
      toast("Khong the luu credential Vertex. Kiem tra JSON va quyen owner.", "error");
    }
  });
})();
