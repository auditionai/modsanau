document.documentElement.classList.add("js");

const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
const finePointer = window.matchMedia("(pointer: fine)");
const root = document.documentElement;
const menuButton = document.querySelector("[data-menu-button]");
const navigation = document.querySelector("[data-nav]");

const starfield = document.querySelector("[data-starfield]");
if (starfield instanceof HTMLCanvasElement && !reducedMotion.matches) {
  const context = starfield.getContext("2d", { alpha: false });
  const stars = [];
  let width = 0;
  let height = 0;
  let density = 0;
  let starFrame = 0;
  let lastTime = performance.now();

  function createStar(initial = false) {
    return {
      x: Math.random() * width,
      y: initial ? Math.random() * height : height + 10,
      size: Math.random() * 1.45 + 0.25,
      speed: Math.random() * 12 + 5,
      drift: (Math.random() - 0.5) * 4,
      alpha: Math.random() * 0.65 + 0.25,
      hue: Math.random() > 0.78 ? (Math.random() > 0.5 ? 190 : 268) : 225
    };
  }

  function resizeStars() {
    const ratio = Math.min(window.devicePixelRatio || 1, 1.75);
    width = window.innerWidth;
    height = window.innerHeight;
    starfield.width = Math.round(width * ratio);
    starfield.height = Math.round(height * ratio);
    starfield.style.width = `${width}px`;
    starfield.style.height = `${height}px`;
    context?.setTransform(ratio, 0, 0, ratio, 0, 0);
    density = Math.min(150, Math.max(70, Math.round((width * height) / 10500)));
    while (stars.length < density) stars.push(createStar(true));
    stars.length = density;
  }

  function drawStars(time) {
    if (!context) return;
    const delta = Math.min(32, time - lastTime) / 1000;
    lastTime = time;
    context.fillStyle = "#060713";
    context.fillRect(0, 0, width, height);
    const glow = context.createRadialGradient(width * 0.56, height * 0.05, 0, width * 0.56, height * 0.05, width * 0.8);
    glow.addColorStop(0, "rgba(37,42,104,.25)");
    glow.addColorStop(0.55, "rgba(13,15,40,.12)");
    glow.addColorStop(1, "rgba(6,7,19,0)");
    context.fillStyle = glow;
    context.fillRect(0, 0, width, height);

    for (let index = 0; index < stars.length; index += 1) {
      const star = stars[index];
      star.y -= star.speed * delta;
      star.x += star.drift * delta;
      if (star.y < -12 || star.x < -12 || star.x > width + 12) stars[index] = createStar(false);
      const pulse = 0.72 + Math.sin(time * 0.0015 + index) * 0.28;
      context.beginPath();
      context.fillStyle = `hsla(${star.hue}, 100%, 88%, ${star.alpha * pulse})`;
      context.arc(star.x, star.y, star.size, 0, Math.PI * 2);
      context.fill();
      if (star.size > 1.35) {
        context.strokeStyle = `hsla(${star.hue}, 100%, 84%, ${star.alpha * 0.25})`;
        context.beginPath();
        context.moveTo(star.x - 5, star.y);
        context.lineTo(star.x + 5, star.y);
        context.moveTo(star.x, star.y - 5);
        context.lineTo(star.x, star.y + 5);
        context.stroke();
      }
    }
    starFrame = requestAnimationFrame(drawStars);
  }

  resizeStars();
  starFrame = requestAnimationFrame(drawStars);
  window.addEventListener("resize", resizeStars, { passive: true });
  document.addEventListener("visibilitychange", () => {
    cancelAnimationFrame(starFrame);
    if (!document.hidden) {
      lastTime = performance.now();
      starFrame = requestAnimationFrame(drawStars);
    }
  });
}

function closeMenu(restoreFocus = false) {
  if (!menuButton || !navigation) return;
  menuButton.setAttribute("aria-expanded", "false");
  navigation.classList.remove("is-open");
  if (restoreFocus) menuButton.focus();
}

if (menuButton && navigation) {
  menuButton.addEventListener("click", () => {
    const isOpen = menuButton.getAttribute("aria-expanded") !== "true";
    menuButton.setAttribute("aria-expanded", String(isOpen));
    navigation.classList.toggle("is-open", isOpen);
  });
  navigation.addEventListener("click", (event) => {
    if (event.target instanceof Element && event.target.closest("a")) closeMenu();
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") closeMenu(true);
  });
  document.addEventListener("click", (event) => {
    if (event.target instanceof Node && !navigation.contains(event.target) && !menuButton.contains(event.target)) closeMenu();
  });
}

const header = document.querySelector("[data-header]");
let scrollFrame = 0;
function updateScrollState() {
  if (scrollFrame) return;
  scrollFrame = requestAnimationFrame(() => {
    header?.classList.toggle("is-scrolled", window.scrollY > 16);
    const process = document.querySelector("#quy-trinh");
    if (process) {
      const rect = process.getBoundingClientRect();
      const span = Math.max(1, rect.height - window.innerHeight * 0.45);
      const progress = Math.min(1, Math.max(0.12, (window.innerHeight * 0.5 - rect.top) / span));
      root.style.setProperty("--progress", progress.toFixed(3));
    }
    scrollFrame = 0;
  });
}
updateScrollState();
window.addEventListener("scroll", updateScrollState, { passive: true });
window.addEventListener("resize", updateScrollState, { passive: true });

if (!reducedMotion.matches && finePointer.matches) {
  let pointerFrame = 0;
  let pointerX = window.innerWidth / 2;
  let pointerY = window.innerHeight / 3;
  window.addEventListener("pointermove", (event) => {
    pointerX = event.clientX;
    pointerY = event.clientY;
    if (pointerFrame) return;
    pointerFrame = requestAnimationFrame(() => {
      root.style.setProperty("--mx", `${pointerX}px`);
      root.style.setProperty("--my", `${pointerY}px`);
      pointerFrame = 0;
    });
  }, { passive: true });

  for (const element of document.querySelectorAll("[data-tilt]")) {
    element.addEventListener("pointermove", (event) => {
      const rect = element.getBoundingClientRect();
      const x = (event.clientX - rect.left) / rect.width;
      const y = (event.clientY - rect.top) / rect.height;
      const depth = Number(element.getAttribute("data-depth") ?? 2);
      element.style.setProperty("--ry", `${(x - 0.5) * depth * 2}deg`);
      element.style.setProperty("--rx", `${(0.5 - y) * depth * 2}deg`);
    });
    element.addEventListener("pointerleave", () => {
      element.style.setProperty("--ry", "0deg");
      element.style.setProperty("--rx", "0deg");
    });
  }

  for (const element of document.querySelectorAll("[data-card-light]")) {
    element.addEventListener("pointermove", (event) => {
      const rect = element.getBoundingClientRect();
      element.style.setProperty("--lx", `${event.clientX - rect.left}px`);
      element.style.setProperty("--ly", `${event.clientY - rect.top}px`);
    });
  }

  for (const button of document.querySelectorAll(".button")) {
    button.addEventListener("pointermove", (event) => {
      const rect = button.getBoundingClientRect();
      button.style.setProperty("--bx", `${event.clientX - rect.left}px`);
      button.style.setProperty("--by", `${event.clientY - rect.top}px`);
    });
  }
}

const toolDeck = document.querySelector("[data-tool-deck]");
if (toolDeck) {
  const tabs = [...toolDeck.querySelectorAll('[role="tab"][data-tool]')];
  const panels = [...toolDeck.querySelectorAll('[role="tabpanel"][data-tool-panel]')];
  const counter = toolDeck.querySelector("[data-tool-counter]");
  const moduleName = toolDeck.querySelector("[data-tool-name]");
  const orbNumber = toolDeck.querySelector(".telemetry-orb span");
  const moduleNames = { editor: "IMAGE LAB", dds: "DDS MATRIX", compare: "VISUAL DIFF", build: "ARCHIVE CORE" };

  function activateTool(nextTab, moveFocus = false) {
    const tool = nextTab.dataset.tool;
    const index = tabs.indexOf(nextTab);
    for (const tab of tabs) {
      const active = tab === nextTab;
      tab.setAttribute("aria-selected", String(active));
      tab.tabIndex = active ? 0 : -1;
    }
    for (const panel of panels) {
      const active = panel.getAttribute("data-tool-panel") === tool;
      panel.hidden = !active;
      panel.classList.toggle("is-active", active);
    }
    const formatted = String(index + 1).padStart(2, "0");
    if (counter) counter.textContent = `${formatted} / 04`;
    if (moduleName) moduleName.textContent = moduleNames[tool] ?? "MODULE";
    if (orbNumber) orbNumber.textContent = formatted;
    if (moveFocus) nextTab.focus();
  }

  tabs.forEach((tab, index) => {
    tab.addEventListener("click", () => activateTool(tab));
    tab.addEventListener("keydown", (event) => {
      let target = index;
      if (event.key === "ArrowRight" || event.key === "ArrowDown") target = (index + 1) % tabs.length;
      else if (event.key === "ArrowLeft" || event.key === "ArrowUp") target = (index - 1 + tabs.length) % tabs.length;
      else if (event.key === "Home") target = 0;
      else if (event.key === "End") target = tabs.length - 1;
      else return;
      event.preventDefault();
      activateTool(tabs[target], true);
    });
  });
}

const gallery = document.querySelector("[data-gallery]");
if (gallery) {
  const image = gallery.querySelector("[data-gallery-image]");
  const label = gallery.querySelector("[data-gallery-label]");
  const description = gallery.querySelector("[data-gallery-description]");
  const count = gallery.querySelector("[data-gallery-count]");
  const buttons = [...gallery.querySelectorAll("button[data-src]")];

  for (const button of buttons) {
    button.addEventListener("click", () => {
      if (!(image instanceof HTMLImageElement)) return;
      for (const item of buttons) item.setAttribute("aria-pressed", String(item === button));
      image.classList.add("is-changing");
      const updateImage = () => {
        image.src = button.dataset.src ?? "";
        image.alt = button.dataset.alt ?? "";
        if (label) label.textContent = button.dataset.label ?? "";
        if (description) description.textContent = button.dataset.description ?? "";
        if (count) count.textContent = button.dataset.count ?? "";
        image.classList.remove("is-changing");
      };
      if (reducedMotion.matches) updateImage();
      else window.setTimeout(updateImage, 150);
    });
  }
}

const appDialog = document.querySelector("[data-app-dialog]");
if (appDialog instanceof HTMLDialogElement) {
  const dialogTitle = appDialog.querySelector("[data-dialog-title]");
  const dialogKicker = appDialog.querySelector("[data-dialog-kicker]");
  const dialogDescription = appDialog.querySelector("[data-dialog-description]");
  const dialogPoints = appDialog.querySelector("[data-dialog-points]");
  const dialogImage = appDialog.querySelector("[data-dialog-image]");
  const dialogCode = appDialog.querySelector("[data-dialog-code]");
  const dialogState = appDialog.querySelector("[data-dialog-state]");
  let dialogTrigger = null;

  const windowContent = {
    overview: { code: "WINDOW / 00", kicker: "WORKSPACE OVERVIEW", title: "Một pipeline thống nhất.", description: "Audition AI Mod Studio tổ chức toàn bộ công việc từ ảnh nguồn đến archive đầu ra trong một workspace riêng.", points: ["Chỉnh sửa trên working copy", "Kiểm tra lại metadata DDS", "Xuất file .ab hoặc .acv độc lập"], image: "/assets/screenshots/home-dark.png", alt: "Màn hình tổng quan Audition AI Mod Studio", state: "LOCAL / READY" },
    tools: { code: "WINDOW / 01", kicker: "CORE TOOLSET", title: "Bốn module làm việc chính.", description: "Image Lab, DDS Matrix, Visual Diff và Archive Core được nối thành một quy trình rõ ràng.", points: ["Chỉnh ảnh và alpha", "Đối chiếu định dạng texture", "Kiểm tra trước khi build"], image: "/assets/screenshots/image-editor.png", alt: "Màn hình Image Editor của Audition AI Mod Studio", state: "4 MODULES / READY" },
    ai: { code: "WINDOW / 02", kicker: "AI STUDIO", title: "AI hỗ trợ, người dùng quyết định.", description: "Kết quả Generate, Edit, Inpaint, Outpaint, Remove, Replace và Upscale luôn được preview trước khi áp dụng.", points: ["Xem kết quả trước khi lưu", "Chi phí Credits do server báo trước", "DDS được kiểm tra lại sau khi duyệt"], image: "/assets/screenshots/image-editor.png", alt: "Không gian chỉnh sửa hình ảnh có hỗ trợ AI", state: "PREVIEW / APPROVAL" },
    interface: { code: "WINDOW / 03", kicker: "INTERFACE PREVIEW", title: "Giao diện Windows hiện tại.", description: "Ảnh chụp được hiển thị nguyên tỷ lệ để bạn xem rõ bố cục ứng dụng mà không bị crop hoặc zoom quá mức.", points: ["Giao diện sáng và tối", "Image Editor chuyên biệt", "Thông báo cập nhật rõ ràng"], image: "/assets/screenshots/home-dark.png", alt: "Màn hình giao diện tối của Audition AI Mod Studio", state: "1920 × 1080 / PREVIEW" },
    pricing: { code: "WINDOW / 04", kicker: "PRICING STATUS", title: "Gói thuê và Credits.", description: "Cấu trúc gồm thời hạn tuần, tháng, năm và các gói nạp Credits. Giá chính thức hiện chưa được công bố.", points: ["Quyền ứng dụng tách khỏi Credits", "Server quyết định số dư và chi phí", "Website chưa tạo giao dịch"], image: "/assets/screenshots/home-dark.png", alt: "Màn hình Audition AI Mod Studio", state: "CATALOG / PENDING" },
    scope: { code: "WINDOW / 05", kicker: "FILE-ONLY SCOPE", title: "Làm việc với file do bạn chọn.", description: "Ứng dụng không tìm game, không điều khiển tiến trình và không tự cài mod vào thư mục Audition.", points: ["Workspace riêng cho từng dự án", "Không sửa pristine template", "Đầu ra là archive độc lập"], image: "/assets/screenshots/home-light.png", alt: "Trang chủ Audition AI Mod Studio", state: "BOUNDARY / VERIFIED" },
    legal: { code: "WINDOW / 06", kicker: "LEGAL INFORMATION", title: "Thông tin pháp lý.", description: "Website không thu thập dữ liệu dự án. Tài khoản, trạng thái xác minh và thiết bị được xử lý qua Supabase theo chính sách quyền riêng tư.", points: ["Mật khẩu không lưu trong bảng ứng dụng", "Không nhúng analytics hoặc tracker", "Bộ cài dùng vùng lưu trữ riêng tư"], image: "/assets/screenshots/home-dark.png", alt: "Giao diện Audition AI Mod Studio", state: "ACCOUNT / PROTECTED" },
    status: { code: "WINDOW / 07", kicker: "RELEASE TELEMETRY", title: "Cổng phát hành đã sẵn sàng.", description: "Người dùng đăng ký, xác minh Gmail và đăng nhập để nhận liên kết tải ngắn hạn khi bộ cài được phê duyệt.", points: ["Nền tảng Windows x64", "Xác thực Supabase", "Private release storage"], image: "/assets/screenshots/update-available.png", alt: "Màn hình thông báo cập nhật Audition AI Mod Studio", state: "RELEASE / AUTH GATED" },
    "ai-approval": { code: "WINDOW / AI", kicker: "USER APPROVAL", title: "Kiểm tra trước khi áp dụng.", description: "AI chỉ tạo bản preview. Bạn cần xem kết quả, xác nhận thay đổi và chờ kiểm tra DDS trước khi ảnh thay thế được lưu.", points: ["So sánh ảnh nguồn và preview", "Xác nhận hoặc hủy kết quả", "Validate DDS sau khi duyệt"], image: "/assets/screenshots/image-editor.png", alt: "Màn hình kiểm tra hình ảnh trước khi áp dụng", state: "WAITING / USER" }
  };

  function openAppWindow(trigger) {
    const key = trigger.getAttribute("data-window") ?? "overview";
    const content = windowContent[key] ?? windowContent.overview;
    dialogTrigger = trigger;
    if (dialogTitle) dialogTitle.textContent = content.title;
    if (dialogKicker) dialogKicker.textContent = content.kicker;
    if (dialogDescription) dialogDescription.textContent = content.description;
    if (dialogCode) dialogCode.textContent = content.code;
    if (dialogState) dialogState.textContent = content.state;
    if (dialogImage instanceof HTMLImageElement) {
      dialogImage.src = content.image;
      dialogImage.alt = content.alt;
    }
    if (dialogPoints) {
      dialogPoints.replaceChildren(...content.points.map((point) => {
        const item = document.createElement("li");
        item.textContent = point;
        return item;
      }));
    }
    if (!appDialog.open) appDialog.showModal();
  }

  for (const trigger of document.querySelectorAll("button[data-window]")) {
    trigger.addEventListener("click", () => openAppWindow(trigger));
  }
  for (const closeButton of appDialog.querySelectorAll("[data-dialog-close]")) {
    closeButton.addEventListener("click", () => appDialog.close());
  }
  appDialog.addEventListener("click", (event) => {
    if (event.target === appDialog) appDialog.close();
  });
  appDialog.addEventListener("close", () => {
    if (dialogTrigger instanceof HTMLElement) dialogTrigger.focus();
  });
}

const revealItems = [...document.querySelectorAll(".reveal")].filter((item) => !item.closest(".hero"));
if ("IntersectionObserver" in window && !reducedMotion.matches) {
  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      entry.target.classList.add("is-visible");
      observer.unobserve(entry.target);
    }
  }, { rootMargin: "0px 0px -8%", threshold: 0.08 });
  revealItems.forEach((item) => observer.observe(item));
} else {
  revealItems.forEach((item) => item.classList.add("is-visible"));
}

for (const year of document.querySelectorAll("[data-year]")) year.textContent = String(new Date().getFullYear());

const authDialog = document.querySelector("[data-auth-dialog]");
const authFeedback = document.querySelector("[data-auth-feedback]");
const publicAuthKey = "aams.public.session";
let publicConfig = null;
let publicSession = null;

async function getPublicConfig() {
  if (publicConfig) return publicConfig;
  const response = await fetch("/app/config", { cache: "no-store" });
  if (!response.ok) throw new Error("Cấu hình dịch vụ hiện chưa sẵn sàng.");
  publicConfig = await response.json();
  return publicConfig;
}

function savePublicSession(session) {
  publicSession = session;
  if (session) localStorage.setItem(publicAuthKey, JSON.stringify(session));
  else localStorage.removeItem(publicAuthKey);
  renderPublicAccount();
}

async function publicAuthRequest(route, body, token = "") {
  const config = await getPublicConfig();
  const headers = { "Content-Type": "application/json", apikey: config.publishableKey };
  if (token) headers.Authorization = `Bearer ${token}`;
  const response = await fetch(`${config.supabaseUrl}/auth/v1/${route}`, {
    method: "POST", headers, body: JSON.stringify(body), cache: "no-store"
  });
  let result = null;
  try { result = await response.json(); } catch { /* bounded generic error below */ }
  if (!response.ok) throw new Error(response.status === 400 ? "Thông tin đăng nhập không hợp lệ hoặc tài khoản chưa xác minh." : "Không thể kết nối dịch vụ tài khoản.");
  return result;
}

async function refreshPublicSession() {
  if (!publicSession?.refresh_token) return false;
  try {
    const next = await publicAuthRequest("token?grant_type=refresh_token", { refresh_token: publicSession.refresh_token });
    savePublicSession(next);
    return Boolean(next?.access_token);
  } catch { savePublicSession(null); return false; }
}

function renderPublicAccount() {
  const signedIn = Boolean(publicSession?.access_token);
  const email = publicSession?.user?.email ?? "";
  document.querySelectorAll("[data-auth-open]").forEach((button) => { button.hidden = signedIn; });
  const download = document.querySelector("[data-release-download]");
  const signout = document.querySelector("[data-auth-signout]");
  if (download) download.hidden = !signedIn;
  if (signout) signout.hidden = !signedIn;
  const state = document.querySelector("[data-account-state]");
  if (state) state.textContent = signedIn ? email : "Chưa đăng nhập";
  const releaseState = document.querySelector("[data-release-state]");
  if (releaseState) releaseState.lastChild.textContent = signedIn ? " Sẵn sàng tải" : " Yêu cầu tài khoản";
}

function selectAuthTab(name) {
  document.querySelectorAll("[data-auth-tab]").forEach((button) => button.setAttribute("aria-selected", String(button.dataset.authTab === name)));
  const signup = document.querySelector("[data-signup-form]");
  const signin = document.querySelector("[data-signin-form]");
  if (signup) signup.hidden = name !== "signup";
  if (signin) signin.hidden = name !== "signin";
  if (authFeedback) { authFeedback.textContent = ""; authFeedback.classList.remove("is-success"); }
}

if (authDialog instanceof HTMLDialogElement) {
  document.querySelectorAll("[data-auth-open]").forEach((button) => button.addEventListener("click", () => authDialog.showModal()));
  document.querySelectorAll("[data-auth-close]").forEach((button) => button.addEventListener("click", () => authDialog.close()));
  document.querySelectorAll("[data-auth-tab]").forEach((button) => button.addEventListener("click", () => selectAuthTab(button.dataset.authTab)));
}

document.querySelector("[data-signup-form]")?.addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = event.currentTarget;
  const data = new FormData(form);
  const displayName = String(data.get("displayName") ?? "").trim();
  const email = String(data.get("email") ?? "").trim().toLowerCase();
  const password = String(data.get("password") ?? "");
  const confirmPassword = String(data.get("confirmPassword") ?? "");
  if (displayName.length < 2 || !/^[^\s@]+@gmail\.com$/i.test(email) || password.length < 8 || password !== confirmPassword) {
    authFeedback.textContent = "Vui lòng nhập tên, địa chỉ @gmail.com và mật khẩu khớp nhau từ 8 ký tự.";
    return;
  }
  try {
    const result = await publicAuthRequest("signup", { email, password, data: { display_name: displayName } });
    if (result?.access_token) { savePublicSession(result); authDialog.close(); return; }
    authFeedback.classList.add("is-success");
    authFeedback.textContent = "Đăng ký thành công. Hãy mở Gmail và xác minh tài khoản, sau đó quay lại đăng nhập.";
    selectAuthTab("signin");
    authFeedback.classList.add("is-success");
    authFeedback.textContent = "Đăng ký thành công. Hãy xác minh Gmail rồi đăng nhập.";
  } catch (error) { authFeedback.textContent = error.message; }
});

document.querySelector("[data-signin-form]")?.addEventListener("submit", async (event) => {
  event.preventDefault();
  const data = new FormData(event.currentTarget);
  try {
    const result = await publicAuthRequest("token?grant_type=password", { email: String(data.get("email") ?? "").trim(), password: String(data.get("password") ?? "") });
    savePublicSession(result);
    authDialog?.close();
    document.querySelector("#phat-hanh")?.scrollIntoView({ behavior: reducedMotion.matches ? "auto" : "smooth" });
  } catch (error) { authFeedback.textContent = error.message; }
});

document.querySelector("[data-auth-signout]")?.addEventListener("click", async () => {
  if (publicSession?.access_token) {
    try { await publicAuthRequest("logout?scope=local", {}, publicSession.access_token); } catch { /* local sign-out still completes */ }
  }
  savePublicSession(null);
});

document.querySelector("[data-release-download]")?.addEventListener("click", async () => {
  const message = document.querySelector("[data-release-message]");
  try {
    const config = await getPublicConfig();
    const send = async () => fetch(`${config.supabaseUrl}/storage/v1/object/sign/${config.releaseBucket}/${config.releaseObject}`, {
      method: "POST", headers: { "Content-Type": "application/json", apikey: config.publishableKey, Authorization: `Bearer ${publicSession.access_token}` },
      body: JSON.stringify({ expiresIn: 120, download: "AuditionAI-Mod-Studio-win-x64.zip" }), cache: "no-store"
    });
    let response = await send();
    if (response.status === 401 && await refreshPublicSession()) response = await send();
    if (!response.ok) throw new Error("Bộ cài chưa được phát hành hoặc tài khoản chưa đủ điều kiện tải.");
    const result = await response.json();
    const signedPath = result.signedURL ?? result.signedUrl;
    if (!signedPath) throw new Error("Không nhận được liên kết tải hợp lệ.");
    location.assign(signedPath.startsWith("http") ? signedPath : `${config.supabaseUrl}/storage/v1${signedPath}`);
  } catch (error) { if (message) message.textContent = error.message; }
});

try { publicSession = JSON.parse(localStorage.getItem(publicAuthKey) || "null"); } catch { localStorage.removeItem(publicAuthKey); }
renderPublicAccount();

const paymentDialog = document.querySelector("[data-payment-dialog]");
const paymentFeedback = document.querySelector("[data-payment-feedback]");
const paymentState = { product: null, order: null, deviceCode: "", pollGeneration: 0 };

async function paymentRequest(route, options = {}) {
  const config = await getPublicConfig();
  const headers = { "Content-Type": "application/json", apikey: config.publishableKey, ...(options.headers || {}) };
  const response = await fetch(`${config.supabaseUrl}/functions/v1/payments/${route}`, { ...options, headers, cache: "no-store" });
  let result = null;
  try { result = await response.json(); } catch { /* generic bounded error below */ }
  if (!response.ok) throw new Error(result?.error || "PAYMENT_REQUEST_FAILED");
  return result;
}

function formatVnd(value) { return `${new Intl.NumberFormat("vi-VN").format(Number(value) || 0)} đ`; }

function productCard(product) {
  const isSubscription = product.type === "subscription";
  const card = document.createElement("article");
  card.className = isSubscription ? "price-card" : "credit-card";
  card.dataset.priceCard = "";
  if (!isSubscription) {
    const coin = document.createElement("div"); coin.className = "credit-coin";
    coin.append(document.createElement("i")); const mark = document.createElement("b"); mark.textContent = "C"; coin.append(mark); card.append(coin);
  }
  const label = document.createElement("span"); label.className = "price-label";
  label.textContent = isSubscription ? `${product.durationDays} ngày` : `${new Intl.NumberFormat("vi-VN").format(product.creditAmount)} Credits`;
  const title = document.createElement("h4"); title.textContent = product.displayName;
  const price = document.createElement(isSubscription ? "p" : "strong");
  price.className = isSubscription ? "price-value" : ""; price.textContent = formatVnd(product.priceVnd);
  const buy = document.createElement("button"); buy.type = "button"; buy.className = "price-state";
  buy.textContent = isSubscription ? "Gia hạn" : "Mua Credits"; buy.addEventListener("click", () => openPurchase(product));
  card.append(label, title, price, buy);
  return card;
}

async function loadPaymentCatalog() {
  const subscriptions = document.querySelector("[data-subscription-products]");
  const credits = document.querySelector("[data-credit-products]");
  if (!subscriptions || !credits) return;
  const creditNote = credits.querySelector(".credit-note");
  try {
    const result = await paymentRequest("catalog");
    const products = Array.isArray(result?.products) ? result.products : [];
    const subscriptionCards = products.filter(item => item?.type === "subscription").map(productCard);
    const creditCards = products.filter(item => item?.type === "credits").map(productCard);
    subscriptions.replaceChildren(...(subscriptionCards.length ? subscriptionCards : [catalogEmpty()]));
    credits.replaceChildren(...(creditCards.length ? creditCards : [catalogEmpty()]));
    if (creditNote) credits.append(creditNote);
  } catch {
    subscriptions.replaceChildren(catalogEmpty("Chưa thể tải bảng giá. Vui lòng thử lại sau."));
    credits.replaceChildren(catalogEmpty("Chưa thể tải bảng giá. Vui lòng thử lại sau."));
    if (creditNote) credits.append(creditNote);
  }
}

function catalogEmpty(message = "Chưa có gói đang phát hành.") {
  const state = document.createElement("p"); state.className = "catalog-state"; state.textContent = message; return state;
}

function openPurchase(product) {
  if (!(paymentDialog instanceof HTMLDialogElement)) return;
  paymentState.product = product; paymentState.order = null; paymentState.pollGeneration += 1;
  document.querySelector("[data-payment-title]").textContent = product.displayName;
  document.querySelector("[data-payment-form]").hidden = false;
  document.querySelector("[data-payment-order]").hidden = true;
  paymentFeedback.textContent = "Kiểm tra Mã thiết bị trước khi tạo đơn.";
  paymentDialog.showModal();
}

function renderPaymentOrder(order) {
  paymentState.order = order;
  const qr = document.querySelector("[data-payment-qr]");
  const qrUrl = new URL(order.qrUrl);
  if (qrUrl.protocol !== "https:" || qrUrl.hostname !== "vietqr.app") throw new Error("PAYMENT_QR_INVALID");
  qr.src = qrUrl.href;
  document.querySelector("[data-payment-product]").textContent = order.productName;
  document.querySelector("[data-payment-amount]").textContent = formatVnd(order.priceVnd);
  document.querySelector("[data-payment-bank]").textContent = `${order.bankCode} · ${order.accountHolder}`;
  document.querySelector("[data-payment-account]").textContent = order.accountNumber;
  document.querySelector("[data-payment-content]").textContent = order.paymentContent;
  document.querySelector("[data-payment-expiry]").textContent = new Date(order.expiresAt).toLocaleString("vi-VN");
  document.querySelector("[data-payment-form]").hidden = true;
  document.querySelector("[data-payment-order]").hidden = false;
}

document.querySelector("[data-payment-form]")?.addEventListener("submit", async event => {
  event.preventDefault(); const form = event.currentTarget;
  if (!form.reportValidity() || !paymentState.product) return;
  const deviceCode = form.deviceCode.value.trim().toUpperCase();
  paymentFeedback.textContent = "Đang tạo đơn thanh toán…";
  try {
    const order = await paymentRequest("orders", { method: "POST", headers: { "X-Idempotency-Key": `web.${crypto.randomUUID()}` },
      body: JSON.stringify({ device_code: deviceCode, product_id: paymentState.product.productId }) });
    paymentState.deviceCode = deviceCode; renderPaymentOrder(order);
    paymentFeedback.textContent = "Đang chờ thanh toán. Vui lòng giữ nguyên nội dung chuyển khoản.";
    pollPayment(order.orderId, ++paymentState.pollGeneration);
  } catch { paymentFeedback.textContent = "Không thể tạo đơn. Hãy kiểm tra Mã thiết bị hoặc thử lại sau."; }
});

async function pollPayment(orderId, generation) {
  const delays = [4000, 5000, 7000, 10000, 15000, 20000, 30000];
  for (let attempt = 0; generation === paymentState.pollGeneration; attempt += 1) {
    await new Promise(resolve => window.setTimeout(resolve, delays[Math.min(attempt, delays.length - 1)]));
    if (generation !== paymentState.pollGeneration) return;
    try {
      const order = await paymentRequest(`orders/${encodeURIComponent(orderId)}?device_code=${encodeURIComponent(paymentState.deviceCode)}`);
      paymentState.order = { ...paymentState.order, ...order };
      if (order.status === "fulfilled") {
        paymentFeedback.textContent = order.productType === "subscription"
          ? `Thanh toán thành công. Đã gia hạn thêm ${order.durationDays} ngày.`
          : `Thanh toán thành công. Đã cộng ${new Intl.NumberFormat("vi-VN").format(order.creditAmount)} Credits.`; return;
      }
      if (order.status === "expired") { paymentFeedback.textContent = "Đơn thanh toán đã hết hạn. Hãy tạo đơn mới."; return; }
      if (order.status === "cancelled") { paymentFeedback.textContent = "Đơn thanh toán đã được hủy."; return; }
      if (order.status === "review_required") { paymentFeedback.textContent = "Thanh toán cần kiểm tra. Không chuyển thêm tiền cho đơn này."; return; }
      paymentFeedback.textContent = "Đang chờ thanh toán. Trạng thái sẽ được cập nhật tự động.";
    } catch { paymentFeedback.textContent = "Không thể cập nhật trạng thái. Hệ thống sẽ thử lại."; }
  }
}

document.querySelector("[data-payment-copy]")?.addEventListener("click", async () => {
  if (paymentState.order?.paymentContent) await navigator.clipboard.writeText(paymentState.order.paymentContent);
});
document.querySelector("[data-payment-cancel]")?.addEventListener("click", async () => {
  if (!paymentState.order) return;
  try {
    await paymentRequest(`orders/${paymentState.order.orderId}?device_code=${encodeURIComponent(paymentState.deviceCode)}`, { method: "DELETE" });
    paymentState.pollGeneration += 1; paymentFeedback.textContent = "Đơn thanh toán đã được hủy.";
  } catch { paymentFeedback.textContent = "Không thể hủy đơn lúc này."; }
});
document.querySelector("[data-payment-close]")?.addEventListener("click", () => paymentDialog?.close());
paymentDialog?.addEventListener("close", () => { paymentState.pollGeneration += 1; });
loadPaymentCatalog();
