import { lstat, readFile, readdir } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const distMode = process.argv.includes("--dist");
const scanRoot = distMode ? path.join(root, "dist") : root;
const manifest = JSON.parse(await readFile(path.join(root, "public-allowlist.json"), "utf8"));
const expected = new Set((distMode ? manifest.buildSourceFiles.map((item) => item.replace(/^web\//, "")) : manifest.repositoryFiles).map(normalize));
const ignoredTopLevel = new Set(distMode ? [] : [".git", "dist", "node_modules"]);
const forbiddenNames = /(^|\/)(\.env($|\.)|id_rsa|id_ed25519|secrets?\.|credentials?\.|appsettings(\.[^.]+)?\.json$)/i;
const forbiddenExtensions = /\.(pfx|p12|pem|key|snk|dll|exe|msi|msix|zip|7z|rar|nupkg)$/i;
const secretPatterns = [
  new RegExp(["SUPABASE", "SERVICE", "ROLE"].join("_"), "i"),
  new RegExp(["BEGIN", "(RSA |EC |OPENSSH )?", "PRIVATE", "KEY"].join(" "), "i"),
  /sk_(live|test)_[A-Za-z0-9]{16,}/,
  /eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}/
];

function normalize(value) {
  return value.split(path.sep).join("/");
}

async function collect(directory, prefix = "") {
  const result = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (!prefix && ignoredTopLevel.has(entry.name)) continue;
    const relative = normalize(path.join(prefix, entry.name));
    const absolute = path.join(directory, entry.name);
    const info = await lstat(absolute);
    if (info.isSymbolicLink()) throw new Error(`Symlink không được phép: ${relative}`);
    if (info.isDirectory()) result.push(...await collect(absolute, relative));
    else if (info.isFile()) result.push(relative);
    else throw new Error(`Loại filesystem không được phép: ${relative}`);
  }
  return result;
}

const actual = (await collect(scanRoot)).sort();
const unexpected = actual.filter((item) => !expected.has(item));
const missing = [...expected].filter((item) => !actual.includes(item)).sort();
if (unexpected.length || missing.length) {
  throw new Error(`Inventory lệch allowlist. Unexpected=[${unexpected.join(", ")}], Missing=[${missing.join(", ")}]`);
}

for (const relative of actual) {
  if (forbiddenNames.test(relative) || forbiddenExtensions.test(relative)) {
    throw new Error(`Artifact bị cấm trong public inventory: ${relative}`);
  }
  const absolute = path.join(scanRoot, relative);
  const info = await lstat(absolute);
  if (info.size > 5 * 1024 * 1024) throw new Error(`File vượt ngân sách 5 MiB: ${relative}`);
  if (/\.(html|css|js|mjs|json|md|toml|txt|yml|svg)$/i.test(relative) || relative.endsWith("_headers")) {
    const content = await readFile(absolute, "utf8");
    for (const pattern of secretPatterns) {
      if (pattern.test(content)) throw new Error(`Mẫu secret bị cấm trong ${relative}: ${pattern}`);
    }
  }
}

console.log(`PUBLIC INVENTORY PASS (${distMode ? "dist" : "repository"}): ${actual.length} file allowlisted, không có secret/artifact bị cấm.`);
