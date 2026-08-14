import { cp, mkdir, readFile, rm, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const dist = path.resolve(root, "dist");
const relativeDist = path.relative(root, dist);

if (!relativeDist || relativeDist.startsWith("..") || path.isAbsolute(relativeDist)) {
  throw new Error(`Từ chối làm sạch thư mục output ngoài admin root: ${dist}`);
}

const manifest = JSON.parse(await readFile(path.join(root, "public-allowlist.json"), "utf8"));
await rm(dist, { recursive: true, force: true });
await mkdir(dist, { recursive: true });

for (const relativeSource of manifest.buildSourceFiles) {
  const source = path.resolve(root, relativeSource);
  const sourceInfo = await stat(source);
  if (!sourceInfo.isFile() || sourceInfo.isSymbolicLink()) {
    throw new Error(`Build source không phải regular file: ${relativeSource}`);
  }

  const relativeOutput = relativeSource.replace(/^web\//, "");
  const output = path.resolve(dist, relativeOutput);
  if (path.relative(dist, output).startsWith("..")) {
    throw new Error(`Output vượt khỏi dist: ${relativeSource}`);
  }

  await mkdir(path.dirname(output), { recursive: true });
  await cp(source, output, { force: true });
}

console.log(`Đã build ${manifest.buildSourceFiles.length} file vào ${dist}`);
