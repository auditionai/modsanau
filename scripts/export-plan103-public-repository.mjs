import { cp, mkdir, readFile, rm, stat } from "node:fs/promises";
import path from "node:path";

const repositoryRoot = path.resolve(import.meta.dirname, "..");
const sourceRoot = path.join(repositoryRoot, "public-release");
const artifactsRoot = path.join(repositoryRoot, "artifacts");
const targetRoot = path.join(artifactsRoot, "plan-103-public-git");
const relativeTarget = path.relative(artifactsRoot, targetRoot);

if (relativeTarget !== "plan-103-public-git" || path.isAbsolute(relativeTarget)) {
  throw new Error(`Từ chối export ngoài target PLAN 103: ${targetRoot}`);
}

const manifest = JSON.parse(await readFile(path.join(sourceRoot, "public-allowlist.json"), "utf8"));
await rm(targetRoot, { recursive: true, force: true });
await mkdir(targetRoot, { recursive: true });

for (const relative of manifest.repositoryFiles) {
  const source = path.join(sourceRoot, relative);
  const info = await stat(source);
  if (!info.isFile() || info.isSymbolicLink()) throw new Error(`Public source không hợp lệ: ${relative}`);
  const target = path.join(targetRoot, relative);
  await mkdir(path.dirname(target), { recursive: true });
  await cp(source, target, { force: true });
}

console.log(`Đã export ${manifest.repositoryFiles.length} file allowlisted vào ${targetRoot}`);
