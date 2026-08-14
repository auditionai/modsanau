document.documentElement.classList.add("js");

const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
const finePointer = window.matchMedia("(pointer: fine)");
const root = document.documentElement;
const menuButton = document.querySelector("[data-menu-button]");
const navigation = document.querySelector("[data-nav]");

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
