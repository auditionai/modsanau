document.documentElement.classList.add("js");

const menuButton = document.querySelector("[data-menu-button]");
const navigation = document.querySelector("[data-nav]");

function closeMenu() {
  if (!menuButton || !navigation) return;
  menuButton.setAttribute("aria-expanded", "false");
  navigation.classList.remove("is-open");
}

if (menuButton && navigation) {
  menuButton.addEventListener("click", () => {
    const expanded = menuButton.getAttribute("aria-expanded") === "true";
    menuButton.setAttribute("aria-expanded", String(!expanded));
    navigation.classList.toggle("is-open", !expanded);
  });

  navigation.addEventListener("click", (event) => {
    if (event.target instanceof HTMLAnchorElement) closeMenu();
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      closeMenu();
      menuButton.focus();
    }
  });

  document.addEventListener("click", (event) => {
    if (event.target instanceof Node && !navigation.contains(event.target) && !menuButton.contains(event.target)) {
      closeMenu();
    }
  });
}

const header = document.querySelector("[data-header]");
const updateHeader = () => header?.classList.toggle("is-scrolled", window.scrollY > 8);
updateHeader();
window.addEventListener("scroll", updateHeader, { passive: true });

const gallery = document.querySelector("[data-gallery]");
if (gallery) {
  const image = gallery.querySelector("[data-gallery-image]");
  const label = gallery.querySelector("[data-gallery-label]");
  const description = gallery.querySelector("[data-gallery-description]");
  const buttons = [...gallery.querySelectorAll("button[data-src]")];

  for (const button of buttons) {
    button.addEventListener("click", () => {
      if (!(image instanceof HTMLImageElement)) return;
      for (const item of buttons) item.setAttribute("aria-pressed", String(item === button));
      image.classList.add("is-changing");
      const nextSource = button.dataset.src ?? "";
      const nextAlt = button.dataset.alt ?? "";
      const applyImage = () => {
        image.src = nextSource;
        image.alt = nextAlt;
        if (label) label.textContent = button.dataset.label ?? "";
        if (description) description.textContent = button.dataset.description ?? "";
        image.classList.remove("is-changing");
      };
      if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) applyImage();
      else window.setTimeout(applyImage, 130);
    });
  }
}

const revealItems = [...document.querySelectorAll(".reveal")];
if ("IntersectionObserver" in window && !window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      entry.target.classList.add("is-visible");
      observer.unobserve(entry.target);
    }
  }, { rootMargin: "0px 0px -8%", threshold: 0.08 });
  for (const item of revealItems) observer.observe(item);
} else {
  for (const item of revealItems) item.classList.add("is-visible");
}

for (const year of document.querySelectorAll("[data-year]")) {
  year.textContent = String(new Date().getFullYear());
}
