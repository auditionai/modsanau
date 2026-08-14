import { createServer } from "node:http";
import { mkdir, readFile, stat } from "node:fs/promises";
import path from "node:path";
import { execFile } from "node:child_process";
import { promisify } from "node:util";

const execute = promisify(execFile);
const repositoryRoot = path.resolve(import.meta.dirname, "..");
const siteRoot = path.join(repositoryRoot, "public-release", "dist");
const evidenceRoot = path.join(repositoryRoot, "artifacts", "plan-103-web-evidence");
const chrome = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
const contentTypes = {
  ".css": "text/css; charset=utf-8",
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".png": "image/png",
  ".svg": "image/svg+xml",
  ".txt": "text/plain; charset=utf-8",
  ".xml": "application/xml; charset=utf-8"
};

await stat(chrome);
await mkdir(evidenceRoot, { recursive: true });

const server = createServer(async (request, response) => {
  try {
    const requestPath = new URL(request.url ?? "/", "http://127.0.0.1").pathname;
    const relative = requestPath === "/" ? "index.html" : decodeURIComponent(requestPath).replace(/^\/+/, "");
    const target = path.resolve(siteRoot, relative);
    if (path.relative(siteRoot, target).startsWith("..")) {
      response.writeHead(403).end("Forbidden");
      return;
    }
    const body = await readFile(target);
    response.writeHead(200, { "Content-Type": contentTypes[path.extname(target)] ?? "application/octet-stream" });
    response.end(body);
  } catch {
    response.writeHead(404).end("Not found");
  }
});

await new Promise((resolve, reject) => {
  server.once("error", reject);
  server.listen(4173, "127.0.0.1", resolve);
});

try {
  for (const capture of [
    { name: "desktop-1440x1100.png", size: "1440,1100" },
    { name: "mobile-500x900.png", size: "500,900" }
  ]) {
    await execute(chrome, [
      "--headless=new",
      "--disable-gpu",
      "--hide-scrollbars",
      "--virtual-time-budget=1500",
      `--window-size=${capture.size}`,
      `--screenshot=${path.join(evidenceRoot, capture.name)}`,
      "http://127.0.0.1:4173/"
    ], { timeout: 60_000 });
  }
  console.log(`Đã capture website vào ${evidenceRoot}`);
} finally {
  await new Promise((resolve) => server.close(resolve));
}
