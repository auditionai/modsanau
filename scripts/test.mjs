import { access, readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const webRoot = path.join(root, "web");
const pages = ["index.html", "privacy.html", "terms.html", "404.html"];
const failures = [];

function expect(condition, message) {
  if (!condition) failures.push(message);
}

for (const page of pages) {
  const html = await readFile(path.join(webRoot, page), "utf8");
  expect(/<html lang="vi"/i.test(html), `${page}: thiếu lang=vi`);
  expect(/<meta name="viewport"/i.test(html), `${page}: thiếu viewport`);
  expect((html.match(/<h1\b/gi) ?? []).length === 1, `${page}: phải có đúng một h1`);
  expect(/<main\b/i.test(html), `${page}: thiếu main landmark`);
  expect(!/<img(?![^>]*\balt=)[^>]*>/i.test(html), `${page}: có img thiếu alt`);
  expect(!/javascript:/i.test(html), `${page}: javascript URL bị cấm`);
  expect(!/target="_blank"/i.test(html), `${page}: không dùng tab mới không cần thiết`);

  const references = [...html.matchAll(/(?:href|src)="(\/[^"]+)"/g)].map((match) => match[1]);
  for (const reference of references) {
    if (reference === "/") continue;
    const resource = reference.split("#")[0].split("?")[0];
    if (!resource || resource.startsWith("/#")) continue;
    try {
      await access(path.join(webRoot, resource.slice(1)));
    } catch {
      failures.push(`${page}: resource không tồn tại ${reference}`);
    }
  }
}

const index = await readFile(path.join(webRoot, "index.html"), "utf8");
expect(/aria-expanded="false"/.test(index), "index.html: menu mobile thiếu aria-expanded");
expect(/aria-pressed="true"/.test(index), "index.html: gallery thiếu trạng thái selection");
expect((index.match(/role="tab"/g) ?? []).length === 4, "index.html: technology deck phải có đúng bốn tab");
expect((index.match(/role="tabpanel"/g) ?? []).length === 4, "index.html: technology deck phải có đúng bốn panel");
expect(/aria-selected="true"/.test(index), "index.html: technology deck thiếu tab mặc định");
expect(/activateTool/.test(await readFile(path.join(webRoot, "script.js"), "utf8")), "script.js: thiếu điều khiển technology deck");
const windowTriggers = [...index.matchAll(/<([a-z]+)\b[^>]*data-window=/gi)];
expect(windowTriggers.length === 10, "index.html: phải có đúng mười nút mở cửa sổ nội trang");
expect(windowTriggers.every((match) => match[1].toLowerCase() === "button"), "index.html: data-window chỉ được đặt trên button, không đặt trên link");
expect(/<dialog\b[^>]*data-app-dialog/.test(index), "index.html: thiếu application dialog");
expect(!/<a\b[^>]*class="[^"]*\bbutton\b/i.test(index), "index.html: CTA dạng button không được điều hướng bằng link");
const script = await readFile(path.join(webRoot, "script.js"), "utf8");
expect(/showModal\(\)/.test(script) && /appDialog\.close\(\)/.test(script), "script.js: thiếu hành vi mở/đóng application dialog");
expect(/prefers-reduced-motion/.test(await readFile(path.join(webRoot, "styles.css"), "utf8")), "styles.css: thiếu reduced-motion");
expect(/Content-Security-Policy:/.test(await readFile(path.join(webRoot, "_headers"), "utf8")), "_headers: thiếu CSP");
expect(!/href="[^"]*(download|\.zip)/i.test(index), "index.html: public download chưa được phê duyệt");

if (failures.length) throw new Error(`PUBLIC SITE TEST FAIL\n- ${failures.join("\n- ")}`);
console.log(`PUBLIC SITE TEST PASS: ${pages.length} trang, liên kết nội bộ, semantic, a11y và release gate.`);
