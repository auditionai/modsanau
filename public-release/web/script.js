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
    legal: { code: "WINDOW / 06", kicker: "LEGAL INFORMATION", title: "Thông tin pháp lý.", description: "Website giới thiệu không thu thập dữ liệu dự án, không có biểu mẫu tài khoản và chưa cung cấp file tải xuống.", points: ["Quyền riêng tư được mô tả minh bạch", "Điều khoản áp dụng cho website thử nghiệm", "Không nhúng analytics hoặc tracker"], image: "/assets/screenshots/home-dark.png", alt: "Giao diện Audition AI Mod Studio", state: "PUBLIC SITE / STATIC" },
    status: { code: "WINDOW / 07", kicker: "RELEASE TELEMETRY", title: "Bản public đang được chuẩn bị.", description: "Website đang hoạt động để giới thiệu sản phẩm. Bản portable chưa được mở tải xuống công khai.", points: ["Nền tảng Windows x64", "Hình thức portable", "Ngày phát hành chưa công bố"], image: "/assets/screenshots/update-available.png", alt: "Màn hình thông báo cập nhật Audition AI Mod Studio", state: "RELEASE / PENDING" },
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
