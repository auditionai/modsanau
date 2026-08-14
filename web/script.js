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
    const open = menuButton.getAttribute("aria-expanded") !== "true";
    menuButton.setAttribute("aria-expanded", String(open));
    navigation.classList.toggle("is-open", open);
  });
  navigation.addEventListener("click", (event) => {
    if (event.target instanceof HTMLAnchorElement || event.target.closest("a")) closeMenu();
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") closeMenu(true);
  });
  document.addEventListener("click", (event) => {
    if (event.target instanceof Node && !navigation.contains(event.target) && !menuButton.contains(event.target)) closeMenu();
  });
}

const header = document.querySelector("[data-header]");
let headerFrame = 0;
function updateHeader() {
  cancelAnimationFrame(headerFrame);
  headerFrame = requestAnimationFrame(() => header?.classList.toggle("is-scrolled", window.scrollY > 18));
}
updateHeader();
window.addEventListener("scroll", updateHeader, { passive: true });

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
      const strength = Number(element.getAttribute("data-tilt-strength") ?? 3);
      element.style.setProperty("--ry", `${(x - 0.5) * strength * 2}deg`);
      element.style.setProperty("--rx", `${(0.5 - y) * strength * 2}deg`);
      element.style.setProperty("--lx", `${x * 100}%`);
      element.style.setProperty("--ly", `${y * 100}%`);
    });
    element.addEventListener("pointerleave", () => {
      element.style.setProperty("--ry", "0deg");
      element.style.setProperty("--rx", "0deg");
      element.style.setProperty("--lx", "50%");
      element.style.setProperty("--ly", "50%");
    });
  }

  for (const element of document.querySelectorAll(".magnetic")) {
    element.addEventListener("pointermove", (event) => {
      const rect = element.getBoundingClientRect();
      element.style.setProperty("--mag-x", `${(event.clientX - rect.left - rect.width / 2) * 0.12}px`);
      element.style.setProperty("--mag-y", `${(event.clientY - rect.top - rect.height / 2) * 0.16}px`);
    });
    element.addEventListener("pointerleave", () => {
      element.style.setProperty("--mag-x", "0px");
      element.style.setProperty("--mag-y", "0px");
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
      const applyImage = () => {
        image.src = button.dataset.src ?? "";
        image.alt = button.dataset.alt ?? "";
        if (label) label.textContent = button.dataset.label ?? "";
        if (description) description.textContent = button.dataset.description ?? "";
        if (count) count.textContent = button.dataset.count ?? "";
        image.classList.remove("is-changing");
      };
      if (reducedMotion.matches) applyImage();
      else window.setTimeout(applyImage, 160);
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
  }, { rootMargin: "0px 0px -7%", threshold: 0.07 });
  for (const item of revealItems) observer.observe(item);
} else {
  for (const item of revealItems) item.classList.add("is-visible");
}

for (const year of document.querySelectorAll("[data-year]")) year.textContent = String(new Date().getFullYear());
