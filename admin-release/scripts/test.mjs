import { access, readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const webRoot = path.join(root, "web");
const failures = [];

function expect(condition, message) {
  if (!condition) failures.push(message);
}

const index = await readFile(path.join(webRoot, "index.html"), "utf8");
const styles = await readFile(path.join(webRoot, "styles.css"), "utf8");
const script = await readFile(path.join(webRoot, "script.js"), "utf8");
const headers = await readFile(path.join(webRoot, "_headers"), "utf8");

expect(/<html lang="vi"/i.test(index), "index.html: thiếu lang=vi");
expect(/<meta name="viewport"/i.test(index), "index.html: thiếu viewport");
expect((index.match(/<h1\b/gi) ?? []).length === 1, "index.html: phải có đúng một h1");
expect(/<main\b/i.test(index), "index.html: thiếu main landmark");
expect(/<form\b/i.test(index), "index.html: thiếu form");
expect(/<dialog\b/i.test(index), "index.html: thiếu dialog xác nhận");
expect(/aria-live="polite"/i.test(index), "index.html: thiếu vùng thông báo aria-live");
expect(/data-api-base/i.test(index), "index.html: thiếu cấu hình API");
expect(/data-device-table/i.test(index), "index.html: thiếu bảng thiết bị");
expect(/data-gift-code-list/i.test(index), "index.html: thiếu danh sách gift code");
expect(!/<img(?![^>]*\balt=)[^>]*>/i.test(index), "index.html: có img thiếu alt");
expect(!/javascript:/i.test(index), "index.html: javascript URL bị cấm");
expect(!/target="_blank"/i.test(index), "index.html: không dùng tab mới");
expect(/AbortController/.test(script), "script.js: thiếu hủy request");
expect(/showModal\(/.test(script) && /close\(/.test(script), "script.js: thiếu luồng dialog");
expect(/localStorage/.test(script), "script.js: thiếu lưu cấu hình cục bộ");
expect(/prefers-reduced-motion/.test(styles), "styles.css: thiếu hỗ trợ reduced-motion");
expect(/backdrop-filter/.test(styles), "styles.css: thiếu glassmorphism");
expect(/Content-Security-Policy:/.test(headers), "_headers: thiếu CSP");
expect(/X-Robots-Tag: noindex/i.test(headers), "_headers: thiếu noindex");

for (const reference of [...index.matchAll(/(?:href|src)="(\/[^"]+)"/g)].map((match) => match[1])) {
  const resource = reference.split("#")[0].split("?")[0];
  if (!resource || resource === "/") continue;
  try {
    await access(path.join(webRoot, resource.slice(1)));
  } catch {
    failures.push(`index.html: tài nguyên không tồn tại ${reference}`);
  }
}

if (failures.length) {
  throw new Error(`ADMIN PORTAL TEST FAIL\n- ${failures.join("\n- ")}`);
}

console.log("ADMIN PORTAL TEST PASS: semantic, a11y, security và static inventory ok.");
