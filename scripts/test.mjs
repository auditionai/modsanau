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
expect((index.match(/role="tab"[^>]*data-tool=/g) ?? []).length === 4, "index.html: technology deck phải có đúng bốn tab");
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
expect(/data-signup-form/.test(index) && /data-signin-form/.test(index), "index.html: thiếu đăng ký/đăng nhập người dùng");
expect(/@gmail\\\.com/.test(script), "script.js: đăng ký chưa giới hạn Gmail theo PLAN 106");
expect(/storage\/v1\/object\/sign/.test(script), "script.js: thiếu tải release private bằng signed URL");
expect(/data-release-download/.test(index) && /hidden/.test(index), "index.html: nút tải phải khóa mặc định");
const appConfigFunction = await readFile(path.join(root, "netlify", "functions", "app-config.mjs"), "utf8");
expect(!/service_role/i.test(appConfigFunction), "app config function must not expose service role");
expect(/releaseBucket/.test(appConfigFunction) && /SUPABASE_PUBLISHABLE_KEY/.test(appConfigFunction), "app config missing public release configuration");

const adminHeaders = await readFile(path.join(webRoot, "_headers"), "utf8");
expect(/\/admin\/\*/.test(adminHeaders) && /X-Robots-Tag: noindex/i.test(adminHeaders), "admin headers missing noindex hardening");
const admin = await readFile(path.join(webRoot, "admin", "index.html"), "utf8");
const adminScript = await readFile(path.join(webRoot, "admin", "script.js"), "utf8");
const adminStyles = await readFile(path.join(webRoot, "admin", "styles.css"), "utf8");
const adminRpcMigration = await readFile(path.join(root, "supabase", "migrations", "202608150002_admin_portal_netlify_supabase.sql"), "utf8");
const adminConfigFunction = await readFile(path.join(root, "netlify", "functions", "admin-config.mjs"), "utf8");
expect(/<html lang="vi"/i.test(admin), "admin/index.html missing lang=vi");
expect((admin.match(/<h1\b/gi) ?? []).length === 1, "admin/index.html must have exactly one h1");
expect(/href="\/admin\/styles\.css"/.test(admin), "admin/index.html stylesheet must be under /admin/");
expect(/src="\/admin\/script\.js"/.test(admin), "admin/index.html script must be under /admin/");
expect(/data-login-form/i.test(admin), "admin/index.html missing secure login form");
expect(!/data-api-base|data-token/i.test(admin), "admin/index.html must not expose manual API/token configuration");
expect(/AbortController/.test(adminScript), "admin/script.js missing request cancellation");
expect(/admin_portal_api/.test(adminScript), "admin/script.js missing Supabase admin RPC");
expect(/grant_type=password/.test(adminScript), "admin/script.js missing Supabase password login");
expect(/\/v1\/admin\/users/.test(adminScript), "admin/script.js missing user management route");
expect(/\/v1\/admin\/transactions/.test(adminScript), "admin/script.js missing transaction search route");
expect(/Authorization/.test(adminScript) && /csrfToken/.test(adminScript) && !/localStorage|sessionStorage/.test(adminScript),
  "admin/script.js must keep authenticated session in memory and attach CSRF correlation");
expect(/auth\.uid\(\)/.test(adminRpcMigration), "admin RPC must derive actor from Supabase JWT");
expect(/SECURITY DEFINER/.test(adminRpcMigration) && /REVOKE ALL[^;]+anon/s.test(adminRpcMigration), "admin RPC missing privilege boundary");
expect(/codycn2804@gmail\.com/.test(adminRpcMigration), "admin RPC missing approved bootstrap owner");
expect(/AS metric_day/.test(adminRpcMigration) && !/::date\s+day\b/.test(adminRpcMigration), "admin analytics uses a PostgreSQL-safe day alias");
expect(/'targetId',\s*recent\.target_id/.test(adminRpcMigration) && /'targetId',e\.target_id/.test(adminRpcMigration), "admin audit target id must be alias-qualified");
expect(!/service_role/i.test(adminConfigFunction), "Netlify config function must not expose privileged Supabase key");
expect(/SUPABASE_URL/.test(adminConfigFunction) && /SUPABASE_PUBLISHABLE_KEY/.test(adminConfigFunction), "Netlify config function missing public Supabase variables");
expect(/prefers-reduced-motion/.test(adminStyles), "admin/styles.css missing reduced-motion");

if (failures.length) throw new Error(`PUBLIC SITE TEST FAIL\n- ${failures.join("\n- ")}`);
console.log(`PUBLIC SITE TEST PASS: ${pages.length} trang, liên kết nội bộ, semantic, a11y và release gate.`);
