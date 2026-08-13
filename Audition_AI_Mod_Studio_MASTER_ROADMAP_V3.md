# AUDITION AI MOD STUDIO — MASTER ROADMAP V3

**Mục đích:** Tài liệu điều khiển Codex theo từng plan để phát triển một ứng dụng Windows thương mại dành cho cộng đồng mod Audition.

**Cơ sở thực tế đã xác minh từ bộ file mẫu và thao tác CMD thực tế của người dùng:**
- Tool archive: `acv.exe` — ACV Tool 5, Windows console executable.
- Archive mẫu: `015.ab` — phải được xem là **Audition Archive**, không hard-code extension `.acv`.
- DDS mẫu: `tn_coby_logo.dds` — **6000 × 1801, DXT5/BC3, 1 mip level**.
- Mapping texture: **relative folder path + exact filename**.
- Lệnh extract thực tế: `acv -da 015.ab 015`.
- Lệnh pack thực tế: `acv -ca 015.ab 015`.
- **ACV Tool 5 là interactive CLI khi chưa có file keydat**: sau khi chạy lệnh, tool hiển thị `SUPPORT COUNTRY LIST` và chờ nhập lựa chọn tại `Select:`.
- Với Audition Việt Nam, người dùng phải nhập **`1` = AuditionVN**.
- Sau lựa chọn vùng, tool tự tạo `015.keydat` trong working directory.
- Khi extract thành công, stdout hiển thị nhiều dòng bắt đầu bằng **`writing :`** và tạo thư mục `015\...`.
- Khi pack thành công, stdout hiển thị nhiều dòng bắt đầu bằng **`Packing:`** và ghi lại archive `015.ab`.
- Nếu `015.keydat` đã tồn tại hợp lệ, automation phải tái sử dụng nó và không được giả định lúc nào tool cũng hỏi vùng.
- Quy trình hiện tại: image/PNG → resize chính xác → DDS → đặt đúng filename/path → extract archive → replace file trong extracted folder → pack archive.
- File template gốc phải bất biến; mọi project thao tác trên working copy.

---

## PRODUCT DIRECTION LOCK — FILE-ONLY ARCHIVE EDITOR

Phần này là ràng buộc sản phẩm có thẩm quyền cao nhất cho toàn bộ roadmap. Nếu một PLAN, ví dụ, tài liệu
phụ hoặc implementation proposal mâu thuẫn với phần này thì **PRODUCT DIRECTION LOCK thắng**. Chỉ product
owner mới có thể thay đổi ràng buộc này bằng requirement mới explicit.

### Product identity và workflow được hỗ trợ

Audition AI Mod Studio là **file-based mod content editor + archive builder + export tool**. Pipeline chuẩn là:

`Input archive/template/project → Create/Open Project → isolated working copy → extract → scan assets →`
`select texture → edit/AI preview → user approval → Apply → DDS validate → build → pack → verify →`
`export standalone .ab/.acv → END`.

Ứng dụng được phép đọc archive, tạo working copy, extract, sửa asset trong workspace, repack working archive,
verify và export final archive. Pristine source/template luôn read-only và không bao giờ là build target writable.

### Absolute non-goals

Không thuộc product scope: detect/discover/validate Audition installation; game path/registry/launcher/folder/
executable/process detection; copy/install mod vào game; backup/restore/patch game file; launch/login/control game;
gameplay automation; in-game/runtime validation; anti-cheat interaction; hooking/injection; memory editing.
Manual in-game observation, nếu bên ngoài thực hiện, chỉ là optional external compatibility QA và không block
application acceptance. Không PLAN tương lai nào được tái tạo các capability này nếu product owner chưa đổi
requirement explicit.

Application installer/updater, code signing và update của **Audition AI Mod Studio itself** là hợp lệ; chúng
không phải Audition game installation hoặc mod installation.

### Source-of-truth hierarchy

- Game: `GameId`; Mod: `(GameId, ModId)`.
- Template: canonical `TemplateId + TemplateVersion` cùng integrity/compatibility metadata.
- Project: `AuditionProject` / `.audproj`.
- Asset: normalized relative path + exact filename.
- DDS: actual metadata đọc qua `IDdsMetadataReader`.
- Image: `InternalImage`; texture state: Texture State Machine; history: `IEditHistoryService`.
- Workspace: isolated managed workspace.
- Build: Atomic Archive Build Pipeline; export: Atomic Archive Export.
- Final deliverable: standalone `.ab`/`.acv` file.

User-selected filesystem destination không được trở thành domain identity hoặc authority cho Game/Mod/Template,
region, archive engine, entitlement hay compatibility.

### Filesystem, process và future-PLAN boundary

- Filesystem mutation chỉ diễn ra trong managed workspace, application-owned state hoặc exact user-selected export
  destination sau validation. Không discover hoặc mutate game installation.
- External process chỉ chạy sau abstraction/policy hiện hữu, bằng absolute trusted path, structured arguments,
  redirected streams, timeout/cancellation và isolated working directory. Không process nào được launch để tìm,
  cài, patch, chạy hoặc quan sát game.
- AI output luôn đi qua `InternalImage → preview → user approval → Apply → DDS pipeline`; AI không bypass project
  validation hoặc tự install output.
- Cloud/auth/credit/payment/catalog được phép nhưng secrets và economic authority ở server; downloaded content vẫn
  đi vào file/workspace pipeline và backend không biết/can thiệp game installation.
- Mọi future PLAN phải nêu goal, dependencies, source of truth, acceptance criteria, persistence/security/failure
  semantics, test gate và explicit non-scope. Thứ tự ưu tiên: local/domain capability → orchestration → UI → gate.

---

# 1. PRODUCT DEFINITION

Xây dựng **Audition AI Mod Studio** cho Windows với workflow:

`Login → Chọn Game → Chọn Mod Type → lấy đúng Archive Template → tạo Project → extract archive → scan DDS → preview/edit/AI → match kích thước/format DDS gốc → replace → validate → pack archive → export final archive`.

Người dùng thông thường không cần biết tên archive thật, câu lệnh CMD, DXT5/BC3 hay cấu trúc folder nội bộ. App phải chuyển các chi tiết đó thành một UX archive editor/builder trực quan.

## Mục tiêu V1 bắt buộc
- Windows desktop app hiện đại.
- Chạy Administrator theo yêu cầu sản phẩm.
- Game catalog + Mod catalog.
- Mỗi Mod Type map tới một archive template riêng (`.ab`, `.acv` hoặc extension được engine hỗ trợ).
- Project system an toàn, không ghi đè template gốc.
- ACV Tool 5 wrapper.
- DDS metadata/preview/encode.
- Resize/crop/canvas với kích thước tùy ý, kể cả NPOT/không phổ biến.
- Match Original DDS.
- Texture browser + thumbnail.
- Replace, reset, compare.
- Batch mapping/replace.
- Build + validate + atomic export `.ab`/`.acv`.
- AI Generate/Edit/Inpaint/Outpaint/Remove/Replace/Upscale qua backend API.
- Supabase Auth/database/storage.
- Credit ledger server-authoritative.
- Prompt presets + project history.
- Security, signing, hardening, update integrity.

---

# 2. TECHNOLOGY BASELINE

Target baseline cho code mới:
- C#
- .NET 10 LTS
- WinUI 3 + Windows App SDK stable
- MVVM
- Dependency Injection
- async/await + CancellationToken + IProgress
- Structured logging
- x64 desktop release; vẫn chạy được child process `acv.exe` 32-bit.

Image/DDS strategy:
- Không tự viết BC compressor từ đầu nếu không cần.
- Ưu tiên Microsoft DirectXTex/texconv hoặc native wrapper quanh DirectXTex cho DDS decode/encode.
- Phải kiểm chứng output bằng DDS mẫu Audition thật và file-pipeline roundtrip; quan sát trong game chỉ là optional external QA.

Backend:
- Supabase PostgreSQL/Auth/Storage.
- Một API/backend trusted layer riêng cho AI, credits, entitlement, template access và payment verification.
- Không đặt privileged Supabase key hoặc AI provider key trong desktop client.

Distribution:
- Signed installer + signed binaries.
- Release build self-contained/unpackaged nếu phù hợp yêu cầu elevation; đóng gói cuối cùng chỉ quyết định sau compatibility test.

---

# 3. NON-NEGOTIABLE ARCHITECTURE RULES

Tạo `AGENTS.md` ở root và bắt Codex tuân thủ:

1. Không bao giờ modify pristine/global archive template.
2. Không hard-code `.acv`; dùng abstraction `AuditionArchive`.
3. Archive identity gồm `templateId + version + hash`.
4. Texture identity = normalized relative path + exact filename.
5. Project luôn dùng working copy.
6. UI không gọi `acv.exe` trực tiếp.
7. Archive commands nằm sau `IAuditionArchiveService` / `IArchiveToolRunner`.
8. DDS operations nằm sau `IDdsService`.
9. Image operations nằm sau `IImageProcessingService`.
10. AI operations nằm sau `IAiService` và backend server.
11. Credits/entitlements do server quyết định; client chỉ hiển thị.
12. Không hard-code secret/key/password/service-role token.
13. Long-running tasks phải async, cancellable, progress-reporting, logged.
14. File replacement phải atomic/safe khi có thể.
15. Destructive operation phải có backup/recovery.
16. Không swallow exceptions.
17. Không tin file/path/input từ user hoặc server nếu chưa validate.
18. Không cho path traversal thoát workspace.
19. Không load DLL/tool từ current working directory tùy ý.
20. Release artifacts phải có integrity/signature verification.
21. Không implement feature ngoài PLAN hiện tại.
22. Trước khi code phải đọc `AGENTS.md`, `docs/ARCHITECTURE.md`, repo hiện tại.
23. Sau mỗi PLAN: build + tests + report files changed + limitations.

---

# 4. SOLUTION STRUCTURE

```text
AuditionModStudio/
├─ src/
│  ├─ AuditionModStudio.App/
│  ├─ AuditionModStudio.Core/
│  ├─ AuditionModStudio.Infrastructure/
│  ├─ AuditionModStudio.Archives/
│  ├─ AuditionModStudio.Dds/
│  ├─ AuditionModStudio.Imaging/
│  ├─ AuditionModStudio.Projects/
│  ├─ AuditionModStudio.Mods/
│  ├─ AuditionModStudio.AI/
│  ├─ AuditionModStudio.Cloud/
│  ├─ AuditionModStudio.Security/
│  └─ AuditionModStudio.Updater/
├─ tests/
│  ├─ Core.Tests/
│  ├─ Archives.Tests/
│  ├─ Dds.Tests/
│  ├─ Imaging.Tests/
│  ├─ Projects.Tests/
│  ├─ Security.Tests/
│  └─ IntegrationTests/
├─ tools/
├─ docs/
├─ scripts/
└─ samples/
```

Không commit commercial secrets hoặc pristine commercial templates vào public repository.

---


# 5. VERIFIED ACV TOOL 5 INTERACTIVE BEHAVIOR

Đây là behavior đã quan sát trực tiếp trên Windows và phải được coi là specification của V1 cho đến khi integration tests chứng minh khác.

## 5.1 Extract thực tế

Working directory ban đầu:

```text
<workspace>\
├─ 015.ab
└─ acv.exe
```

Command:

```cmd
acv -da 015.ab 015
```

Nếu chưa có `015.keydat`, ACV Tool 5 không extract ngay. Nó hiển thị:

```text
Keydat file not found. Program will automatic generate it. Please Select
===== SUPPORT COUNTRY LIST =====
1. AuditionVN
2. AuditionSEA
...
99. Manual Input
Select:
```

Với game Audition Việt Nam, automation phải cung cấp:

```text
1
```

qua **stdin của child process**, tương đương người dùng tự gõ `1` rồi Enter.

Sau đó tool:
1. tạo `015.keydat`;
2. tạo thư mục `015`;
3. extract file;
4. xuất progress dạng:

```text
writing : 015\texture\...\file.dds
writing : 015\texture\...\file.png
```

## 5.2 Pack thực tế

Command:

```cmd
acv -ca 015.ab 015
```

Nếu `015.keydat` không tồn tại, tool lại có thể yêu cầu chọn country giống extract. Automation phải nhập `1` cho profile AuditionVN.

Khi pack chạy, progress có dạng:

```text
Packing: 015\texture\...\file.dds
Packing: 015\texture\...\file.png
```

Kết thúc, working `015.ab` được cập nhật.

## 5.3 Keydat lifecycle

`015.keydat` là **runtime companion artifact** của ACV Tool 5, không phải file project do người dùng chỉnh sửa.

Quy tắc:
- Tên keydat dự kiến theo base name archive: `015.ab` → `015.keydat`; phải xác minh bằng test, không hard-code rải rác.
- Keydat nằm trong isolated project/tool working directory.
- Nếu keydat hợp lệ đã tồn tại, tái sử dụng để tránh prompt.
- Nếu thiếu keydat, runner dùng `GameRegionProfile` để cung cấp lựa chọn country.
- AuditionVN profile V1: `CountrySelection = "1"`.
- Không dùng mouse/keyboard automation, SendKeys hoặc mở CMD thật. Phải điều khiển child process bằng redirected stdin/stdout/stderr.
- Không parse thành công chỉ dựa vào exit code; phải kết hợp exit code + expected output/artifacts + validation.
- Không xem `keydat` như secret/DRM boundary vì tool có thể tự tạo nó từ country selection.
- Không dùng global shared keydat giữa nhiều build đang chạy đồng thời; mỗi workspace có artifact riêng hoặc cache được copy an toàn.

## 5.4 Required process model

Production wrapper phải dùng tương đương:

```text
UseShellExecute = false
CreateNoWindow = true
RedirectStandardInput = true
RedirectStandardOutput = true
RedirectStandardError = true
WorkingDirectory = isolated workspace
```

Arguments phải truyền bằng structured argument list, không ghép chuỗi shell từ input user.

Runner cần state machine tối thiểu:

```text
Starting
→ WaitingForCountrySelection (chỉ khi keydat thiếu / prompt xuất hiện)
→ Extracting | Packing
→ VerifyingArtifacts
→ Completed | Failed | Cancelled | TimedOut
```

Progress parser nhận biết:
- `writing :` → Extract progress;
- `Packing:` → Pack progress;
- `Keydat file not found` / `Select:` → Country-selection interaction.

**Lưu ý kỹ thuật:** prompt `Select:` có thể không kết thúc bằng newline. Không được thiết kế chỉ dựa vào `ReadLineAsync()` để phát hiện prompt. Có thể:
1. kiểm tra trước việc keydat có tồn tại và pre-seed stdin theo profile; và/hoặc
2. đọc stdout theo chunk/character để nhận prompt không có newline.

---

# 6. STAGE GATES — KHÔNG ĐƯỢC BỎ QUA


## GATE A — REAL ARCHIVE EXTRACT + INTERACTIVE COUNTRY SELECTION
Trên Windows thật:
- Copy `015.ab` và approved `acv.exe` sang isolated temp workspace.
- Đảm bảo test case đầu tiên **không có `015.keydat`**.
- Chạy `acv -da 015.ab 015`.
- Redirect stdin/stdout/stderr; **không mở CMD**.
- Runner tự chọn `1` cho `AuditionVN`.
- Xác nhận `015.keydat` được tạo.
- Xác nhận stdout có progress `writing :`.
- Xác nhận folder `015` được tạo và scan được texture.
- Chạy extract lần hai với keydat đã tồn tại và xác nhận không bị treo do automation chờ prompt không xuất hiện.
- Original global fixture hash không đổi.

**Không pass Gate A → không code AI/store.**

## GATE B — REAL DDS ROUNDTRIP
Với `tn_coby_logo.dds`:
- đọc 6000×1801;
- đọc DXT5/BC3;
- mip = 1;
- decode preview;
- encode replacement đúng size/format;
- đọc lại header xác nhận.

## GATE C — REAL REPLACE + PACK PRODUCT GATE
- Tạo replacement đúng metadata của một DDS an toàn trong working copy.
- Chỉ intended asset được thay đổi; non-target assets giữ nguyên integrity.
- Pack bằng production ACV Tool 5 pipeline và tạo archive non-empty trong controlled output.
- Re-extract bằng production archive pipeline; inventory đầy đủ, intended content đúng và không phát hiện
  corruption qua extract/scan pipeline.
- Pristine fixture/template không bị mutate; artifact có SHA-256 xác định và không track runtime/proprietary
  artifact ngoài source deliverable dự kiến.
- Full build, regression và security verification PASS.

Manual launch Audition, visual confirmation và gameplay/runtime compatibility là **OPTIONAL EXTERNAL
COMPATIBILITY QA**. Ứng dụng không launch, đăng nhập, điều khiển hoặc quan sát runtime game; QA này không
block Gate C trong phạm vi acceptance hiện tại và không được tuyên bố đã thực hiện nếu chưa có bằng chứng.

**Chỉ sau Product Gate C mới coi archive/DDS generation pipeline đã chứng minh trong phạm vi ứng dụng.**

---

# PHASE A — FOUNDATION & REAL FILE POC

## PLAN 01 — Repository & Solution Foundation

**Codex task:**
Create the initial Audition AI Mod Studio solution using .NET 10 and WinUI 3. Create all projects from the architecture section, configure clean references, nullable, DI, logging, test projects, `AGENTS.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md`, and `docs/ROADMAP.md`. Do not implement ACV, DDS, cloud, AI, or business UI yet. Build and run tests.

**Acceptance:** solution builds cleanly; Core has no UI/cloud implementation dependency.

## PLAN 02 — Windows App Bootstrap + Administrator

Implement WinUI 3 bootstrap, global DI, structured logging, crash handling and Windows manifest with `requireAdministrator` because this product currently requires elevation from launch. Keep privilege-sensitive logic isolated so a future split into non-elevated UI + elevated broker remains possible.

Acceptance:
- UAC prompt on launch.
- logs under LocalAppData.
- no application writes to installation directory for normal data.

## PLAN 03 — App Paths & Secure Workspace Paths

Create `IAppPaths` and validated locations:
- Projects
- Cache
- Temp
- Logs
- Downloads
- SecureTemplateCache
- Backups

Use canonical paths; reject path traversal; randomize temporary working directories; never concatenate untrusted relative paths without validation.

## PLAN 04 — Settings System

Strongly typed JSON settings with atomic save, versioning and validation. Store only non-secret configuration. Secrets/tokens must use secure storage service introduced later.

## PLAN 05 — Real Sample Fixture Registration

Add fixture metadata for the provided real samples without altering originals:
- `acv.exe`
- `015.ab`
- `tn_coby_logo.dds`

Tests must copy fixtures into per-test temp folders before mutation. Record expected DDS metadata: width 6000, height 1801, DXT5/BC3, 1 mip.

---

# PHASE B — ARCHIVE ENGINE

## PLAN 06 — ACV Tool 5 Interactive Process Runner

Implement `IArchiveToolRunner` and `AcvTool5Runner` based on the verified real CLI behavior.

Commands:

```text
Extract: acv -da <archiveFile> <extractFolder>
Pack:    acv -ca <archiveFile> <extractFolder>
```

This is **not a fire-and-forget CLI**. When `<archiveBase>.keydat` is missing, ACV Tool 5 asks for a country selection through stdin.

Requirements:
- use absolute path to `acv.exe`;
- `UseShellExecute = false`;
- `CreateNoWindow = true`;
- redirect stdin;
- redirect stdout;
- redirect stderr;
- explicit isolated working directory;
- arguments supplied without shell concatenation;
- timeout;
- `CancellationToken`;
- async process lifecycle;
- kill child process tree on cancellation/timeout where supported;
- capture raw stdout/stderr for diagnostics;
- sanitize paths before logging;
- expose structured progress events;
- do not assume `.ab` or `.acv` extension.

Add:

```text
AcvTool5Operation
AcvTool5RunRequest
AcvTool5RunResult
AcvTool5Progress
AcvTool5RunnerState
GameRegionProfile
KeydatStatus
```

`GameRegionProfile` V1 must support:

```text
RegionId: audition_vn
DisplayName: AuditionVN
AcvToolCountrySelection: "1"
```

Interaction strategy:
1. Determine expected keydat path for the working archive.
2. If valid keydat is missing, runner must be ready to provide configured country selection.
3. Do not use `SendKeys`, UI Automation, mouse automation, PowerShell keystrokes or a visible CMD window.
4. Send selection using child process `StandardInput`.
5. Do not rely only on newline-based stdout parsing because `Select:` may not end with newline.
6. Must support both:
   - first run: keydat missing, selection required;
   - later run: keydat exists, no selection required.
7. Do not hang if prompt does not appear.

Progress parser:
- `writing :` => extract item/progress;
- `Packing:` => pack item/progress;
- country menu / `Select:` => awaiting region selection;
- known error text => structured error.

Success cannot be based only on process exit code. Validate expected artifacts after process exits.

Tests:
- command argument construction;
- archive/folder names containing spaces and Unicode;
- missing executable;
- cancellation;
- timeout;
- stdout chunk parser where `Select:` has no newline;
- fake process fixture requiring stdin `1`;
- fake process fixture where keydat already exists and no stdin is requested;
- progress parsing for `writing :` and `Packing:`.

Acceptance:
- wrapper is UI-independent;
- no visible CMD;
- can automate the exact manual workflow shown by the product owner.

## PLAN 07 — Keydat Lifecycle + ACV Tool Integrity

Implement a dedicated `IKeydatService` plus ACV Tool integrity policy.

Keydat requirements:
- derive expected keydat path from archive/tool behavior through one centralized strategy;
- detect missing/present/invalid keydat;
- generated keydat belongs to the project/tool workspace, not the global pristine template;
- preserve it for subsequent pack in the same project;
- safely copy or regenerate for a rebuild when needed;
- never expose a "keydat editor" to ordinary users;
- keydat is **not** considered a secret security boundary;
- concurrent projects must never race on one shared writable keydat.

Tool integrity:
- approved `acv.exe` SHA-256/version manifest;
- verify before every production execution or at trusted startup/cache validation;
- use absolute executable path;
- reject unexpected replacement outside explicit developer/admin registration flow;
- do not treat hash verification as DRM.

Add tests:
- archive `015.ab` expects/produces a companion keydat in isolated workspace;
- missing keydat => region selection path;
- existing keydat => no unnecessary prompt wait;
- tool hash mismatch => operation rejected.

## PLAN 08 — Audition Archive Abstraction

Create models:
- `AuditionArchiveTemplate`
- `ArchiveEngineType`
- `ArchiveWorkspace`
- `ArchiveCommandResult`

Support `.ab`, `.acv`, and future extensions through metadata, not code branches scattered through the UI.

## PLAN 09 — Project Archive Workspace

Create pristine-reference → working archive → extracted directory lifecycle.

Important change for security:
- global template must never be modified;
- project uses a working copy;
- do not expose a global raw-template folder through UI;
- workspace permissions should be limited to required accounts;
- cleanup abandoned temp workspaces.

## PLAN 10 — Real Extract Integration POC With AuditionVN Selection

Windows-only integration test using a **copy** of real `015.ab` and approved `acv.exe`.

Test A — first run without keydat:
1. Create isolated temp workspace.
2. Copy `015.ab` and `acv.exe`.
3. Assert `015.keydat` does not exist.
4. Run:
   `acv -da 015.ab 015`
5. Automatically provide stdin selection `1` for AuditionVN.
6. Capture full output.
7. Assert `015.keydat` was generated.
8. Assert folder `015` exists.
9. Assert extracted file count > 0.
10. Assert progress parser observed at least one `writing :` entry.
11. Scan the extracted tree and write a diagnostic inventory.
12. Assert original fixture SHA-256 remains unchanged.

Test B — second run with keydat:
1. Reuse/copy the generated keydat into a clean working workspace as appropriate.
2. Run extract again.
3. Confirm runner completes without waiting forever for a country prompt that does not occur.

Test C — paths:
Repeat with a working directory containing spaces and Vietnamese Unicode characters, similar to the real user path.

**STOP and document exact behavior if real output differs from these assumptions. Do not hide discrepancies by weakening assertions.**

## PLAN 11 — Recursive Asset Scanner

Scan extracted archive and build deterministic asset index:
- relative path
- filename
- extension
- byte size
- SHA-256
- last modified
- category candidate

DDS files become `TextureAsset`.

## PLAN 12 — Real Archive Repack POC + Keydat Cases

Without changing extracted content, repack a copied workspace.

Test A — keydat present:
- start from successful extract workspace containing `015.keydat`;
- run `acv -ca 015.ab 015`;
- capture progress;
- assert at least one `Packing:` entry;
- validate output archive exists, non-zero and changed timestamp/hash as expected;
- never compare archive hash for byte-identical equality unless ACV Tool 5 is proven deterministic.

Test B — keydat intentionally missing:
- use another disposable copy;
- remove only working `015.keydat`;
- run pack;
- runner must provide AuditionVN selection `1`;
- assert keydat is generated;
- assert pack completes.

Test C — failure safety:
- cancel/kill a pack in disposable workspace;
- global pristine template must remain unchanged;
- partial working output must not be promoted as a successful build.

Only after DDS engine is ready, repeat pack with one controlled texture replacement and test resulting archive in Audition.

---

# PHASE C — DDS ENGINE

## PLAN 13 — DDS Metadata Reader

Implement robust DDS header reader supporting legacy + DX10 headers.

Read:
- width/height
- mip count
- FourCC
- DXGI format where present
- BC1/2/3/4/5/6H/7 recognition
- alpha capability/flags
- header type
- pixel data offset

Real fixture test must assert `tn_coby_logo.dds = 6000×1801, DXT5/BC3, mip 1`.

## PLAN 14 — DirectXTex Evaluation Harness

Integrate Microsoft DirectXTex/texconv in a controlled prototype or native wrapper. Do not yet hide it behind production UX. Determine exact commands/API necessary to:
- decode DDS to preview;
- encode PNG/RGBA to DXT5/BC3;
- preserve exact target dimensions including unusual sizes;
- force mip count;
- preserve legacy header where required.

Document findings.

## PLAN 15 — DDS Preview Service

Implement `IDdsPreviewService` using the chosen engine. Decode off UI thread, preserve alpha, return a UI-safe bitmap representation, cache later.

## PLAN 16 — Image → DDS Encoder

Implement production `IDdsEncoder` behind an abstraction. Input is internal RGBA image + `DdsTargetSettings`.

Must support the formats actually discovered in Audition samples. Do not implement every DDS format before it is needed.

## PLAN 17 — Match Original DDS

Given original target DDS, generate settings automatically:
- exact width
- exact height
- compression/format
- mip count
- header compatibility
- alpha behavior

Default user mode = `Match Original`.

## PLAN 18 — DDS Validation

After encoding, reopen DDS and validate against target metadata. Do not replace extracted target until validation passes.

## PLAN 19 — Real DDS Roundtrip Gate

Use `tn_coby_logo.dds` as real test fixture. Encode a known image to 6000×1801 DXT5, mip 1. Read it back and compare required metadata. Historical manual game-test direction is superseded by the file-only product boundary confirmed after PLAN 50.

---

# PHASE D — IMAGE ENGINE

## PLAN 20 — Internal Image Model & Import

Support PNG/JPEG/WebP/BMP and create a normalized RGBA working representation with size, alpha, source metadata and safe disposal.

## PLAN 21 — Arbitrary Resize Engine

Support any validated positive integer dimensions. Do not force power-of-two.

Modes:
- Stretch
- Fit
- Fill
- Keep Aspect
- Free Aspect
- Manual Crop
- Canvas Resize
- Transparent Padding
- align center/top/bottom/left/right

Tests include 6000×1801, 378×126, 137×512, 64×17.

## PLAN 22 — Interactive Crop/Transform Model

Non-destructive transform state:
- zoom
- pan
- crop rectangle
- scale
- rotate
- flip
- translate
- reset

## PLAN 23 — Image Adjustments Core

Implement non-destructive parameters for brightness, contrast, exposure, saturation, vibrance, hue, temperature, tint, highlights, shadows, gamma, sharpen, blur, opacity.

## PLAN 24 — Edit History / Undo Redo

Create operation history independent from UI. Include snapshot strategy and memory budget.

## PLAN 25 — Alpha Channel Utilities

View/extract/replace/invert/threshold alpha. This is important for DDS textures with transparency.

---

# PHASE E — GAME / MOD TEMPLATE INTELLIGENCE

## PLAN 26 — Game Catalog

Create `GameDefinition` and catalog provider. First game is Audition, but architecture must not bind all screens to Audition-specific filenames.

## PLAN 27 — Mod Definition

Each Mod Type has:
- id
- gameId
- displayName
- category
- cover image
- description
- archive template id/version
- archive filename (e.g. `015.ab`)
- extract folder name (e.g. `015`)
- archive engine (`ACVTool5`)
- game/region profile (e.g. `audition_vn`)
- ACV Tool 5 country selection mapping resolved through profile (`AuditionVN` => `1`)
- keydat strategy (`reuse-or-generate` by default)
- install relative path
- compatibility information

User sees semantic mod name, not raw archive filename.

`install relative path` ở đây là metadata lịch sử của PLAN 27 và không cấp authority để discover, validate
hoặc mutate game installation. Product boundary sau PLAN 50 chỉ export archive tới user-selected destination.

## PLAN 28 — Texture Manifest

Map real relative DDS paths to friendly metadata:
- displayName
- category
- description
- tags
- preview
- editable flag
- recommended edit mode

Fallback gracefully to raw filename/path if manifest missing.

## PLAN 29 — Smart Mod Scan

For an archive without a complete manifest:
1. extract;
2. scan DDS;
3. read metadata;
4. generate thumbnails;
5. group by folder;
6. mark unknown semantics;
7. allow admin to label later.

## PLAN 30 — Template Versioning

Template identity = `templateId + version + SHA256 + compatible game build`.

Project stores exact version. Never silently migrate old projects.

---

# PHASE F — PROJECT SYSTEM

## PLAN 31 — Project Model `.audproj`

Store:
- projectId/name
- gameId/modId
- templateId/version/hash
- working archive path/reference
- extracted root
- edited texture records
- image assets
- AI assets
- edit state/history
- build state
- timestamps
- schema version

## PLAN 32 — Create Project Workflow

User flow:
`Select Game → Select Mod → Project Name → Create`.

Engine flow:
- entitlement check if template is premium;
- acquire correct template;
- resolve game/region profile;
- create workspace;
- make working archive;
- prepare/reuse/regenerate workspace keydat policy;
- extract through interactive ACV Tool 5 runner, automatically supplying AuditionVN selection when required;
- scan DDS;
- load manifest;
- cache metadata;
- save project.

Rollback partial project on failure.

## PLAN 33 — Load/Recover Project

Do not extract every time. Validate state and recover missing cache. Re-extract only when needed.

## PLAN 34 — Texture State Machine

States:
- Original
- Modified
- AI Generated
- Pending
- Invalid
- Missing

## PLAN 35 — Reset Texture / Reset Project

Reset individual texture from project-safe pristine reference or regenerated workspace. Reset Project restores a fresh working copy without touching global template.

---

# PHASE G — CACHE & PERFORMANCE

## PLAN 36 — Thumbnail Cache

Content-hash keyed thumbnails, disk + memory cache, async generation, corruption recovery.

## PLAN 37 — Lazy Loading

Load metadata first, thumbnail second, full texture only when selected.

## PLAN 38 — Background Task Manager

Central queue for extract/scan/thumbnail/resize/convert/build/download/AI jobs. Progress + cancel + notifications.

## PLAN 39 — Temp Cleanup & Crash Recovery

Track workspaces with lock/session files. On next launch detect stale temp folders and offer safe cleanup/recovery.

---

# PHASE H — PROFESSIONAL UI/UX

## PLAN 40 — Design System

Implement reusable tokens/components for:
- modern gaming UI
- WinUI acrylic/mica/glass-inspired panels
- colorful gradients
- rounded cards
- subtle glow/depth
- 3D press/hover buttons
- consistent spacing/typography
- dark/light/theme support if desired

Clarity and performance over decoration.

## PLAN 41 — App Shell

Sidebar:
Home / Projects / AI Studio / Image Editor / Mod Library / Batch / Cloud / Settings.

Top bar:
account / credits / notifications / connection status.

## PLAN 42 — Home: Game → Mod First

The first creation path must be:
1. choose game;
2. show compatible Mod Types;
3. choose mod;
4. create project.

Do not begin from filesystem ACV selection for normal users.

## PLAN 43 — Project Workspace UI

Left: folder tree/search/filter.
Center: preview/editor.
Right: metadata/edit options.
Status: target size, format, state, validation.

## PLAN 44 — Texture Grid + Search

Cards with thumbnail, friendly name, raw filename, size, status. Filters: Modified/Original/Invalid/AI/category/size/alpha.

## PLAN 45 — Crop/Resize Canvas UI

Interactive target frame showing exact DDS dimensions. Support zoom/pan/crop/fit/fill/stretch/canvas/padding.

## PLAN 46 — Before/After Compare

Side-by-side, slider, toggle; checkerboard for alpha.

## PLAN 47 — Apply Texture UX

On Apply:
- target validate;
- resize/crop;
- match original DDS;
- temporary encode;
- validate;
- atomic replace in extracted workspace;
- history entry;
- state Modified;
- regenerate thumbnail;
- save project.

---

# PHASE I — BUILD / EXPORT

## PLAN 48 — Project Validator

Structured errors/warnings/info for:
- missing archive/folder/files;
- invalid paths;
- wrong filename;
- wrong dimensions;
- wrong DDS format;
- malformed DDS;
- pending edit;
- tool integrity issue.

Đã triển khai `IProjectValidator` dưới dạng boundary chỉ-đọc. Validator đối chiếu exact project với
retained workspace, kiểm tra archive/thư mục/path, quét lại cây extracted qua scanner an toàn, đọc lại
header DDS và so sánh dimensions/format với metadata baseline immutable được lưu lúc tạo project.
Thiếu metadata baseline là error fail-closed và vẫn kiểm tra khả năng đọc DDS; thiếu file, DDS phát sinh sai tên,
pending edit hoặc ACV Tool 5 không đạt integrity là error chặn build. Kết quả chỉ gồm severity, kind,
diagnostic code giới hạn và normalized relative texture identity; không chứa absolute path, raw exception,
process output hay trusted hash. Validation không sửa project, cache, texture hoặc archive.

## PLAN 49 — Build Pipeline

`Save → Validate → temp build workspace → pack via ACV Tool 5 → verify output → hash output → copy to project Output`.

Statuses: Preparing/Validating/Packing/Verifying/Completed/Failed/Cancelled.

Đã triển khai `IProjectBuildService` đúng thứ tự trên. Build lưu snapshot trước khi validate; chỉ tiếp tục
khi `IProjectValidator.CanBuild=true`. Working archive và extracted tree được copy có giới hạn vào một
secure workspace ngẫu nhiên, từ chối reparse point và xác minh working archive hash trước khi pack.
`IAuditionArchiveService` là boundary duy nhất chạy pack; project working archive và pristine template
không bị mutate. Artifact sau pack được mở kiểm tra độc lập, hash SHA-256, copy durable rồi promote
atomically vào `BuildOutput/Output/<exact archive filename>`. Build state chỉ thành `Succeeded` sau khi
atomic `.audproj` save cuối đạt; save/cancellation/exception sau promotion rollback output cũ. Temporary
build workspace luôn được dispose và không được retain.

## PLAN 50 — Real Replace + Pack Gate

Replace only `tn_coby_logo.dds` or another safe test texture in a copy of `015.ab`; preserve target metadata,
pack bằng production pipeline, re-extract và chứng minh intended/non-target integrity, pristine safety cùng
artifact SHA-256. Đây là Product Gate C. Manual test trong Audition chỉ là optional external compatibility QA
và không block PLAN 50 trong current application acceptance scope.

## PLAN 51 — Export Destination

Cho phép user chọn nơi lưu final `.ab`/`.acv` archive. Model strongly typed phải hỗ trợ user-selected output
directory, output filename, archive-extension validation, canonical absolute path, Unicode/spaces, collision
detection, explicit overwrite policy và structured errors. Destination là arbitrary writable filesystem
location, không phải trusted game metadata; không derive GameId/ModId/TemplateId/RegionProfile/engine từ path.

Validation phân biệt invalid path/name/extension, unavailable destination, access denied, reparse policy và
existing-file collision. Không silently overwrite. Nếu lưu last export directory thì dùng application settings,
không lưu machine-local absolute path vào `.audproj`.

## PLAN 52 — Atomic Archive Export

Xuất final validated build artifact của PLAN 49 tới destination PLAN 51 theo pipeline:
`Validated Build Artifact → destination validation → temporary export → copy/write → hash verification →
atomic promote → final archive`.

Không rebuild archive qua pipeline thứ hai. Failure giữ nguyên existing destination và cleanup/recover temp.
Overwrite chỉ chạy với explicit policy và phải bảo toàn destination cũ cho tới khi candidate sẵn sàng promote.
Final file phải tồn tại, non-empty, đúng extension/type contract và byte/hash-identical với build artifact.

## PLAN 53 — Build & Export UI

UI workflow `Validate → Build → Export`: hiển thị project/build status, chọn output filename/destination,
start, progress, cancellation, success/failure và final output path. ViewModel gọi application services;
View/code-behind không copy file. Reuse PLAN 38 Background Task Manager để không block UI và propagate
structured progress/cancellation.

UI không có Find/Detect/Install/Restore/Launch/Login Audition hoặc Game Path. Success có thể hiển thị filename,
size, SHA-256 và output location; không launch game.

---

# PHASE J — BATCH

## PLAN 54 — Batch Build & Export

Hỗ trợ nhiều job độc lập theo flow `Project → Validate → Build → Export → per-job result`. Reuse PLAN 38,
bounded concurrency và không spawn unlimited pack processes. Destination name deterministic; detect conflict
trước execution khi có thể và không để hai jobs ghi cùng path ngoài explicit conflict policy.

Per-job state là typed equivalent của `Queued/Validating/Building/Exporting/Succeeded/Failed/Cancelled`;
failure/cancellation một job không corrupt job khác. Batch này là project/archive build-export, không phải game
install và không thay thế texture batch mapping đã mô tả trong lịch sử product nếu feature đó được lập PLAN lại.

## PLAN 55 — File-Only Production Gate

Chứng minh end-to-end pipeline:
`archive/template → Create Project → scan → select/edit/replace texture → Apply → Project Validate → Build →
pack → verify → Export final .ab/.acv`.

Gate PASS khi project workflow, intended DDS replacement/metadata, validator, production pack, archive
verification/re-extract phù hợp, expected inventory, intended/non-target integrity, pristine safety, final export,
standalone artifact size/hash, full regression và security/artifact audits đều PASS.

Gate không yêu cầu Audition installation, game directory, game launch/login/gameplay, visual in-game validation,
replacement trong game hoặc backup game files.

## PLAN 56 — Batch Build Summary

One archive build after approved replacements. Summary of changed/failed/skipped textures.

---

# PHASE K — AI & CLOUD

## PLAN 57 — AI Provider Abstraction

`IAiService` operations:
Generate / Edit / Inpaint / Outpaint / RemoveObject / ReplaceObject / Upscale.

Desktop never calls provider using embedded privileged secret.

## PLAN 58 — Supabase Auth

Sign up/sign in/sign out/session refresh/password reset/profile. Store session secrets via secure Windows storage abstraction, not plain JSON.

## PLAN 59 — Backend Trusted Gateway

Create server endpoints for AI/credits/template entitlement. Validate access token and request schema. Server owns provider credentials.

## PLAN 60 — Credit Ledger

Tables/services for wallets + immutable-ish transaction ledger + reservations + refunds. Use transactional operations and idempotency keys.

Client cannot set balance, cost, refund or successful payment state.

## FUTURE PLAN EXECUTION CONTRACT

Áp dụng bắt buộc cho mọi PLAN chưa triển khai từ PLAN 61 trở đi, ngoài acceptance riêng của từng PLAN:

- **Dependencies/source of truth:** chỉ reuse contract/service đã nêu; không tạo source of truth thứ hai.
- **Persistence:** mọi schema change phải explicit, có migration/backward-compatibility và server/local ownership rõ.
- **Security:** validate input/ownership/size/path; secrets và commercial authority ở server; log được redaction.
- **Failure semantics:** typed failure, fail closed tại trust boundary, không partial success; mutation phải atomic/
  transactional, tác vụ dài phải async/cancellable/progress-reporting.
- **Test gate:** targeted + affected + full regression; Debug/Release x64, XAML nếu có UI, format, dependency/
  secret/artifact scans. Integration cần fixture thật thì phải báo limitation, không đổi assertion thành skip.
- **Absolute non-scope:** toàn bộ game-installation/launch/runtime non-goals trong PRODUCT DIRECTION LOCK.

## PLAN 61 — AI Pricing

**Goal:** server-hosted, versioned pricing rules cho từng AI operation; client chỉ fetch display estimate.

**Dependencies/source:** PLAN 57 AI operations, PLAN 59 Gateway, PLAN 60 ledger; server rule version là authority.

**Acceptance:** bounded authenticated price-query contract; actual capture cost luôn resolve lại server-side theo
rule version; client amount bị ignore/reject; deterministic rounding/currency-credit units, cache/version semantics,
tests cho stale estimate và rule change.

**Persistence/security/failure:** pricing schema/config chỉ server writable; unavailable/invalid rule fail closed và
không reserve/charge. **Non-scope:** AI job execution, payment, pricing UI ngoài response display model.

## PLAN 62 — AI Job System

**Goal:** durable server job state machine `Pending/Queued/Processing/Completed/Failed/Cancelled`.

**Dependencies/source:** PLAN 57, 59–61; persisted job row là state authority, ledger là credit authority.

**Acceptance:** persist verified owner, bounded input metadata, opaque output reference, reserved/final credits,
timestamps và provider request ID; transactional enqueue/reserve, idempotent transitions, retry/lease recovery,
cancel semantics và guaranteed release/refund on terminal provider failure.

**Persistence/security/failure:** DB migration + ownership/RLS; no raw image/secret in logs. **Non-scope:** desktop UI,
image Apply, client-selected charge, gameplay/runtime work.

## PLAN 63 — AI Studio UI

**Goal:** WinUI/MVVM surface cho prompt, optional negative prompt, model/quality/aspect/reference, price estimate và history.

**Dependencies/source:** PLAN 38 task manager, PLAN 57 client abstraction, PLAN 58 auth, PLAN 61–62 server APIs;
server job/history và pricing response là authority.

**Acceptance:** accessible loading/empty/error/offline states, cancellation, bounded input, no UI-thread blocking;
completed result chỉ thành `InternalImage` preview và chưa mutate project.

**Persistence/security/failure:** chỉ non-secret presentation preference local; token qua secure auth service.
**Non-scope:** mask editor, Apply/DDS replacement, local provider key, auto-install output.

## PLAN 64 — AI Mask Editor

**Goal:** non-destructive mask editor cho brush/erase/size/hardness/opacity/clear/invert/show-hide.

**Dependencies/source:** `InternalImage`, PLAN 24 history và PLAN 63 selected preview; mask pixels ở source-image
coordinate space là authority.

**Acceptance:** exact dimension/alignment under zoom/pan, deterministic strokes, undo/redo, bounded memory,
accessible controls và cancellation cho expensive composition; serialization chỉ khi job request cần.

**Persistence/security/failure:** mask asset nằm trong project workspace, atomic save; invalid/missing source fail
without mutation. **Non-scope:** provider operation orchestration, DDS Apply, game/runtime interaction.

## PLAN 65 — Inpaint / Outpaint / Remove / Replace / Upscale

**Goal:** nối từng AI operation vào job system và editor pipeline, triển khai/test độc lập từng operation.

**Dependencies/source:** PLAN 47 Apply, PLAN 57, PLAN 62–64; provider result chỉ là preview `InternalImage`, project
history sau Apply mới là local edit authority.

**Acceptance:** operation-specific schema/mask/dimension validation; preview→explicit approval→existing Apply→Match
Original→DDS validation; cancellation/failure không charge sai hoặc mutate project; applied result có undo/history.

**Persistence/security/failure:** reuse job/ledger/project stores, không raw provider error. **Non-scope:** bypass
validator, silent Apply, auto-build/export/install.

## PLAN 66 — Prompt Presets

**Goal:** preset local/cloud versioned, tagged theo operation, `(GameId, ModId)` và texture semantic type.

**Dependencies/source:** PLAN 28 manifest, PLAN 57 operation types, PLAN 63 UI; immutable preset ID+version là authority.

**Acceptance:** deterministic merge precedence, schema validation, safe import/export, cloud ownership/entitlement,
offline local fallback và no silent migration.

**Persistence/security/failure:** local presets atomic/versioned; cloud presets authenticated. Corrupt/conflicting
preset bị isolate. **Non-scope:** executable prompt action, provider secret, automatic Apply/build.

---

# PHASE L — SECURITY ARCHITECTURE

> **Security principle:** Desktop anti-crack measures are delay/hardening layers, not a perfect security boundary. Anything that must remain truly secret or economically authoritative belongs server-side.

## PLAN 67 — Threat Model

**Dependencies/source:** PRODUCT DIRECTION LOCK, implemented PLAN 01–66 architecture and real deployment design.

Create `docs/THREAT_MODEL.md` with assets and attackers.

Assets:
- AI provider secret
- Supabase privileged keys
- credit balances/transactions
- payment state
- premium template catalog
- raw pristine archive templates
- mod manifests
- app binary/IP
- update channel
- user tokens

Threats:
- decompile .NET code;
- patch license/credit checks;
- steal API keys;
- copy raw template archives;
- inspect temp directory;
- dump process memory;
- replace `acv.exe`;
- DLL hijacking;
- MITM/replay API requests;
- fake payment callback;
- concurrent credit spending;
- tamper update package;
- path traversal/malicious file input.

Rank risks and mitigations.

**Acceptance:** trust/data-flow diagrams, ranked abuse cases, owner/mitigation/test mapping và explicit residual risk.
Không coi game process, anti-cheat hoặc mod installation là asset/workflow của application.

## PLAN 68 — Secret Separation

**Dependencies/source:** PLAN 58–62 auth/gateway/job boundaries; deployed secret store là authority.

Audit entire solution. Enforce:
- no AI provider key in client;
- no Supabase service-role key in client;
- no payment webhook secret in client;
- no archive master encryption key in client;
- no secrets in source control/logs/crash reports.

Add automated secret scanning to CI.

**Acceptance:** scan source/build/log/crash artifacts; negative tests; rotation/runbook; client contains no privileged
credential. Không thay bằng obfuscation hoặc local hidden configuration.

## PLAN 69 — Secure Local Session Storage Audit

Audit và harden implementation PLAN 58 (`ISecureSessionStore`/Windows Credential Manager); không tạo secure store
thứ hai. Store refresh/session material only when necessary; never plaintext token in settings/project/log.

Threat-model an Administrator-level local attacker separately; DPAPI is not magic against a fully compromised/elevated account.

**Dependencies/source:** PLAN 58 là implementation authority, PLAN 67 threat model. **Acceptance:** migration/
compatibility behavior, rotation/delete/corruption/concurrency tests và release plaintext scan. Failure fail closed.

## PLAN 70 — Server-Authoritative License/Entitlement

**Dependencies/source:** PLAN 58–59 verified identity/Gateway; server entitlement record là authority.

Premium capabilities require server entitlement. Use short-lived signed server grants/tokens containing user, scope, template/mod ids, expiry, nonce/audience as appropriate.

Never rely only on a local `IsPremium=true` boolean.

Define offline behavior explicitly; premium AI must not work offline.

**Acceptance:** signed/scoped/expiring grants, ownership/audience/replay tests và fail-closed offline semantics.
Không local `IsPremium` authority hoặc game-install coupling.

## PLAN 71 — Premium Template Distribution Model

**Dependencies/source:** PLAN 30 exact template identity, PLAN 59 entitlement, PLAN 70 grant.

Do **not** bundle raw premium `.ab/.acv` templates openly in installer.

Implement template packages:
- server-controlled catalog;
- authenticated authorization;
- short-lived download URLs;
- package hash/signature;
- encryption at rest/in transit;
- version + manifest metadata.

Important: document that if the client must ultimately obtain/build a complete exported archive, a determined authorized user can potentially recover its contents. The goal is to prevent casual harvesting and unauthorized download, not claim impossible secrecy.

Ở đây “complete archive” chỉ là standalone exported file; không hàm ý app cài/chạy game. **Acceptance:** authenticated
catalog/download, short-lived URL, signed/hash-verified package, exact version and revocation tests. Failure không
publish package vào cache/workspace.

## PLAN 72 — Encrypted Local Template Cache

**Dependencies/source:** PLAN 03 managed paths, PLAN 30 identity, PLAN 71 package format.

If local cache is required:
- store encrypted package, not raw global template;
- random per-template data key;
- authenticated encryption such as AES-GCM;
- key material never hard-coded;
- local key wrapping through secure Windows storage and/or short-lived server authorization;
- integrity metadata.

On use, decrypt only when needed into controlled workspace and clean up promptly.

**Acceptance:** authenticated encryption/key wrapping, atomic cache write, corruption/key-loss cleanup/recovery,
concurrent access tests. Cache không phải DRM và không chứa game-install path.

## PLAN 73 — Template Exposure & Build-Location Decision

Codex must explicitly analyze the workflow limitation:
- ACV Tool 5 requires a file/folder accessible on disk;
- a project working archive can resemble the pristine archive;
- final archive may contain unchanged original assets.

Design options:
A. client-side build — easier/faster, weaker template secrecy;
B. server-side build worker — stronger control over pristine template delivery but final output may still be extractable;
C. hybrid — metadata/template package controlled server-side, build local.

Produce a written ADR trước commercial release, chọn local/server/hybrid theo legal, latency, offline và exposure.
Final output vẫn đi qua export boundary; server build không được install output hoặc biết game path.

## PLAN 74 — Protected Workspace Hardening

**Dependencies/source:** PLAN 03/09/39 managed workspace, PLAN 72 cache, PLAN 67 threats.

For local archive processing:
- random working directory name;
- restrictive NTFS ACL where practical;
- no predictable shared temp path;
- no plaintext global template cache;
- cleanup on success/failure/startup;
- avoid writing secrets to filenames;
- never log raw keys.

Do not promise secure deletion on SSD; cleanup is best-effort exposure reduction.

**Acceptance:** ACL/reparse/cleanup/crash/concurrency tests; failure giữ evidence an toàn và không mutate pristine.

## PLAN 75 — Privilege Model Review

**Goal:** review `requireAdministrator` từ PLAN 02 trong bối cảnh file-only editor và user-selected export.

**Dependencies/source:** PLAN 02 bootstrap, PLAN 03 paths, PLAN 67 threat model, PLAN 74 workspace hardening.

**Acceptance:** inventory mọi privileged operation; chứng minh nhu cầu elevation hoặc thiết kế migration sang
unelevated app/least-privilege broker; protocol/path allowlist, UAC, upgrade/backward-compatibility và test plan rõ.

**Persistence/security/failure:** đây là review/ADR; không đổi manifest/elevation trong PLAN này. **Non-scope:** game
installation authority, silent self-elevation hoặc implementation broker chưa được PLAN riêng phê duyệt.

## PLAN 76 — App Code Signing

Sign release EXE/DLL/installer with Authenticode certificate and timestamp signatures. CI verifies signatures before publish. Private signing key must live in secure CI secret store/HSM/certificate service, never repository.

## PLAN 77 — App Update Signing & Verification

Updater accepts only releases with expected publisher/signature plus signed manifest/hash. Download to temp, verify, then install. Prevent downgrade unless explicitly supported. Protect against replacement of update metadata.

## PLAN 78 — Binary Obfuscation Strategy

Evaluate a reputable .NET obfuscator for Release builds:
- symbol renaming;
- control-flow/string/resource protection where stable;
- anti-tamper options only after AV/compatibility testing.

Do not obfuscate Debug/dev builds. Keep mapping files private for crash symbolization.

Obfuscation is a cost-increasing layer, not where secrets are stored.

## PLAN 79 — Native AOT / Native Core Evaluation

Evaluate WinUI/.NET Native AOT support for Release and/or move selected sensitive pure-compute libraries into AOT/native code if compatible. Measure compatibility with WinUI, DirectXTex interop, Supabase client, serializers and plugins.

Do not adopt AOT solely as “uncrackable DRM.” Use it only if stable and beneficial.

## PLAN 80 — Client Integrity / Anti-Tamper Checks

Add moderate integrity checks:
- signed executable verification where appropriate;
- critical companion tool hash verification;
- manifest hash verification;
- detect modified resource bundles.

Failure should disable risky operations and produce diagnostics. Avoid aggressive anti-debug tricks that cause false positives or malware-like behavior.

## PLAN 81 — DLL Hijacking / Process Launch Hardening

Use absolute paths for native libraries/tools. Control DLL search paths. Do not execute files from user-controlled folders. Validate archive/tool paths. Avoid `cmd.exe /c` when direct process invocation is possible. Quote arguments and prevent command injection.

## PLAN 82 — API Replay / Abuse Protection

Backend:
- TLS;
- auth validation;
- short-lived access tokens;
- idempotency key for charged operations;
- request nonce/timestamp where useful;
- rate limiting;
- size/type limits;
- ownership checks;
- audit logs.

## PLAN 83 — Supabase RLS Hardening

Audit every user-owned table after PLAN 60 migrations. Users read only permitted rows; wallet/ledger mutation remains
through exact server functions. Add migration privilege/RLS tests and cross-user negative integration tests; do not
create a second wallet schema.

## PLAN 84 — Payment Security

Credit is granted only after server verifies payment provider callback/webhook. Make processing idempotent. Client “payment success” screen is never proof of payment.

## PLAN 85 — Credit Concurrency Integration Gate

Verify PLAN 60 transactional reservation/capture/release/refund against a real PostgreSQL test instance. Add parallel
race, duplicate key, conflicting payload, capture-vs-release and over-refund tests. Fix the existing ledger/functions
if evidence fails; do not create another credit service or let client set amounts.

## PLAN 86 — Device Sessions / Abuse Controls

Optional commercial controls:
- device registration count;
- session list/revocation;
- unusual login detection;
- rate limits.

Avoid brittle invasive hardware fingerprint DRM in V1 unless business need is proven.

## PLAN 87 — Privacy & Logging Security

Redact:
- passwords;
- auth tokens;
- signed URLs;
- provider secrets;
- payment secrets.

Diagnostic export excludes user images/templates by default.

## PLAN 88 — Dependency / Supply-Chain Security

Pin/lock dependency versions appropriately, scan vulnerabilities, maintain SBOM, verify third-party/native tool provenance, document licenses, and review redistribution rights for `acv.exe`, templates and game assets before commercial distribution.

## PLAN 89 — Security Test Matrix

Tests must include:
- modified `acv.exe` rejected;
- corrupt template rejected;
- path traversal rejected;
- forged local credit ignored;
- expired token rejected;
- duplicate AI charge idempotent;
- concurrent wallet spend safe;
- wrong user cannot read another project/job;
- update with wrong signature rejected;
- leaked plaintext token search returns none;
- tampered template package rejected;
- app survives malformed DDS/archive inputs safely.

**Trạng thái triển khai:** hoàn tất ở PLAN 89. Ma trận 12/12 có runner fail-closed và bằng chứng unit/integration/real
PostgreSQL/real ACV tại `docs/SECURITY_TEST_MATRIX.md`; Supabase staging/live vẫn chưa được xác minh.

## PLAN 90 — Release Penetration / Crack-Resistance Review

Before paid beta, commission/manual review focused on:
- decompilation exposure;
- local license patch attempts;
- API request manipulation;
- template harvesting paths;
- temp/cache extraction;
- RLS bypass attempts;
- update tampering;
- DLL search/path attacks.

Fix architectural weaknesses first; do not rely on adding more obfuscation to hide broken authorization.

**Trạng thái triển khai:** internal manual review hoàn tất ở PLAN 90 với 8/8 attack path. Runtime chuyển sang unelevated
`asInvoker`; public Release artifact loại/reject PDB/source/private map/key/proprietary fixture trước signing. Server authority
không đổi. Paid beta vẫn NO-GO khi thiếu production signer/updater/CDN, live Supabase/Stripe evidence, redistribution approval
và security-owner/external review theo risk policy. Xem `docs/RELEASE_PENETRATION_REVIEW.md`.

---

# PHASE M — COMMERCIAL PLATFORM

## PLAN 91 — Remote Product Catalog (Game/Mod/Template Taxonomy)

Backend-managed catalog of semantic `GameId`, `(GameId, ModId)`, manifests, exact template versions, file-format/
build compatibility và entitlement requirements. Cache signed/validated catalog locally.

**Dependencies/source:** PLAN 26–30 local catalogs, PLAN 59/70 entitlement, PLAN 71 packages. Remote signed version is
distribution authority; local cache is derived. **Acceptance:** schema/signature/version/rollback/offline-cache tests,
exact identity mapping and atomic cache. **Non-scope:** install path, executable/launcher/registry/game discovery.

## PLAN 92 — File-Only Template Admin Tool

Admin-only workflow:
archive upload → engine select → extract test → DDS scan → label textures → version → compatibility → publish encrypted/signed template package metadata.

**Dependencies/source:** PLAN 11/13/28/30, PLAN 71 distribution; uploaded archive is explicit admin input and
published exact template version is authority. **Acceptance:** role authorization, isolated scan, validation,
immutable version, audit trail and atomic publish. **Non-scope:** game installation discovery/mutation or mod install.

## PLAN 93 — Account/Profile/Credit History UI

Server-sourced profile, wallet, usage and transaction history. Reuse PLAN 58 auth/PLAN 60 ledger; UI is read-only
for balances/transactions, has accessible loading/error/empty states and never treats cached/client values as authority.

## PLAN 94 — Payment Abstraction

Backend payment provider abstraction; verified idempotent webhook → PLAN 60 ledger grant transaction. Payment
provider event is source of payment truth; client success page cannot grant credit. Add signature/replay/order/
duplicate/refund tests; provider outage leaves typed pending/retriable state, not fabricated success.

## PLAN 95 — Audition AI Mod Studio Application Installer

Signed installer for **Audition AI Mod Studio itself**, clean uninstall, preserve user projects unless explicitly
deleted, verify prerequisites/application files and follow PLAN 75 privilege decision. It must not detect/install/
patch Audition game or copy an exported mod into game directories.

## PLAN 96 — Audition AI Mod Studio Application Updater

Signed release channel for **Audition AI Mod Studio itself**, staged rollout, rollback strategy and update deferral
while project build/export is active. Reuse PLAN 77 signature verification; never launch/update Audition game.

---

# PHASE N — QUALITY / RELEASE

## PLAN 97 — Archive File-Pipeline Integration Test Suite

Use copies of real Audition fixtures. Extract → scan → replace → pack. Original fixture hashes remain unchanged.

Add build/verify/export/re-extract coverage, Unicode paths, corruption/cancellation and non-target integrity. Test
ends at standalone archive; no installation or runtime game dependency.

## PLAN 98 — DDS File-Pipeline Compatibility Matrix

Build a growing corpus of real DDS samples from each Mod Type. Record width/height/format/mips/alpha/header and file-pipeline compatibility status through decode/encode/validate/archive roundtrip evidence.

## PLAN 99 — Crash/Recovery Tests

Kill app during extract, edit, encode, pack, export, download. Verify workspace, export destination và temporary transaction recover safely.

## PLAN 100 — Performance Tests

Measure archive scan, thumbnail generation, 6000×1801 resize/BC3 encode, memory usage, large batch operations and UI responsiveness.

## PLAN 101 — V1 File-Only Product Release Gate

Release only when:
- Gate A/B/C pass on real Audition data;
- pristine template policy verified;
- atomic export collision/overwrite/rollback tested;
- no privileged secrets in desktop binary;
- Auth/RLS/credit race/idempotency tested;
- installer/update signing active;
- template distribution policy legally reviewed;
- security test matrix pass;
- paid AI failure/refund paths verified.

- PLAN 75 privilege decision resolved before packaging;
- no game-install/detect/launch/runtime capability or dependency exists in product acceptance pipeline;
- final deliverable remains standalone `.ab`/`.acv` and application install/update affects only this application.

---

# 7. PRIORITY ORDER — DO NOT BUILD OUT OF ORDER

## MILESTONE 1 — Technical Proof
Plans 01–19.

Deliverable: actual `015.ab` extract + actual DDS read/encode pipeline.

## MILESTONE 2 — One Real Mod End-to-End
Plans 20–35 + 48–50.

Deliverable: choose one Mod Type → edit one real texture → build → verify/export standalone archive.

## MILESTONE 3 — Editor Completion
Plans 36–56.

Deliverable: project browser, professional editor UX, build/export/batch.

## MILESTONE 4 — AI Workflow & Commercial Backend
Plans 57–66 + core security 67–85.

Deliverable: accounts, AI, credits, server authority.

## MILESTONE 5 — Application Hardening, Distribution & Public Paid Beta
Plans 86–101.

Deliverable: signed, hardened, updateable commercial application.

---

# 8. MASTER CODEX PREFIX

Copy this before every PLAN:

```text
You are working on Audition AI Mod Studio, a commercial Windows desktop application for creating Audition texture mods.

Before coding:
1. Read AGENTS.md.
2. Read docs/ARCHITECTURE.md.
3. Read docs/SECURITY.md and docs/THREAT_MODEL.md if they exist.
4. Inspect the current repository and existing abstractions.
5. Implement ONLY the current plan.
6. Do not rewrite unrelated modules.
7. Never modify a pristine/global Audition archive template.
8. Never hard-code the archive extension; real fixtures include 015.ab.
9. Texture identity is relative path + exact filename.
10. Real DDS fixture tn_coby_logo.dds is 6000x1801, DXT5/BC3, mip count 1.
11. ACV Tool 5 is interactive when keydat is missing. For AuditionVN, automatically supply stdin selection `1`.
12. Extract progress is identified by `writing :`; pack progress is identified by `Packing:`.
13. `Select:` may not end with a newline. Do not implement prompt handling using only ReadLineAsync.
14. Do not use visible CMD, SendKeys, mouse automation or Windows UI Automation for ACV Tool interaction. Use redirected stdin/stdout/stderr.
15. Generated keydat is a working companion artifact, not a DRM/security secret.
16. UI must never launch acv.exe directly.
17. All long operations must be async, cancellable, progress-reporting and logged.
18. Never add AI provider secrets, Supabase service-role keys, payment secrets or template master encryption keys to the desktop client.
19. Client credit/license values are not authoritative.
20. Validate untrusted paths/files and prevent path traversal/command injection.
21. Add/update tests.
22. Build the full solution.
23. Run relevant tests.
24. Fix regressions introduced by this task.
25. At completion report: files changed, design decisions, tests/build results, security implications, known limitations, and recommended next PLAN.
26. PRODUCT DIRECTION LOCK thắng mọi conflicting wording: không detect/install/backup/restore/launch/login/control
    Audition game, không runtime/in-game automation/validation; pipeline kết thúc tại exported `.ab`/`.acv`.

Current task:
[PASTE ONE PLAN HERE]
```

---

# 9. FIRST TASK TO COPY INTO CODEX

```text
Create the technical foundation for a new commercial Windows project named Audition AI Mod Studio.

Known real-world inputs:
- acv.exe is the existing ACV Tool 5 command-line tool used to unpack/repack Audition archives.
- A real archive sample is named 015.ab, therefore DO NOT hard-code .acv as the archive extension.
- Verified commands are:
  extract: acv -da 015.ab 015
  pack:    acv -ca 015.ab 015
- ACV Tool 5 is interactive when the matching keydat file is missing:
  it prints SUPPORT COUNTRY LIST and waits at Select:.
- For Audition Vietnam, selection `1` = AuditionVN.
- After selection, ACV Tool 5 creates `015.keydat`.
- Extract progress prints lines beginning with `writing :`.
- Pack progress prints lines beginning with `Packing:`.
- Production automation must redirect child-process stdin/stdout/stderr and must not use visible CMD/SendKeys/UI automation.
- A real DDS sample tn_coby_logo.dds is 6000x1801, DXT5/BC3, mip count 1.
- Texture mapping is relative folder path + exact filename.
- Global pristine archive templates must never be modified.

For this task implement foundation only:
1. Create a .NET 10 solution.
2. Use WinUI 3 for the Windows desktop app.
3. Create projects:
   AuditionModStudio.App
   AuditionModStudio.Core
   AuditionModStudio.Infrastructure
   AuditionModStudio.Archives
   AuditionModStudio.Dds
   AuditionModStudio.Imaging
   AuditionModStudio.Projects
   AuditionModStudio.Mods
   AuditionModStudio.AI
   AuditionModStudio.Cloud
   AuditionModStudio.Security
   AuditionModStudio.Updater
4. Create corresponding test projects.
5. Configure clean references, nullable, DI and structured logging.
6. Create AGENTS.md with strict architecture/security rules.
7. Create docs/ARCHITECTURE.md.
8. Create docs/SECURITY.md explaining server-authoritative secrets/credits and that client obfuscation is only a hardening layer.
9. Create docs/ROADMAP.md.
10. Create a minimal WinUI app shell that launches.
11. Add app manifest scaffolding for requireAdministrator but keep privileged operations isolated behind abstractions.
12. Do NOT implement ACV extraction, DDS conversion, Supabase, AI, licensing, encryption, obfuscation or store UI yet.
13. Build the solution and run tests.
14. Return repository tree, project dependency graph, files created, build/test status and next recommended task.
```

---


# 10. IMMEDIATE CODEX TASK AFTER FOUNDATION — INTERACTIVE ACV TOOL 5 POC

Sau khi PLAN 01–05 hoàn thành, copy nguyên task này vào Codex:

```text
Implement the real ACV Tool 5 interactive runner and integration POC for Audition AI Mod Studio.

Real verified behavior:
- Tool executable: acv.exe
- Real archive: 015.ab
- Extract command: acv -da 015.ab 015
- Pack command: acv -ca 015.ab 015
- If 015.keydat is missing, ACV Tool 5 prints a SUPPORT COUNTRY LIST and waits for input at Select:.
- Audition Vietnam is selection 1 (AuditionVN).
- After selection, the tool generates 015.keydat.
- During extract, progress lines begin with "writing :".
- During pack, progress lines begin with "Packing:".
- The Select: prompt may not be newline terminated.

Requirements:
1. Implement IArchiveToolRunner / AcvTool5Runner in the archive module.
2. Do not open a visible CMD window.
3. Do not use SendKeys, keyboard simulation, mouse automation, UI Automation, or shell scripts to answer the prompt.
4. Use ProcessStartInfo with UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true, RedirectStandardOutput=true and RedirectStandardError=true.
5. Use an explicit isolated working directory.
6. Use ArgumentList or equivalent safe structured arguments. Do not concatenate untrusted values into a shell command.
7. Add GameRegionProfile with audition_vn => display name AuditionVN => AcvToolCountrySelection "1".
8. Implement keydat path/status handling behind a dedicated service/strategy.
9. On a first run where 015.keydat is absent, supply "1" + newline through StandardInput.
10. Do not assume the prompt will be read by ReadLineAsync because Select: may not have a trailing newline. Implement chunk-based prompt/progress parsing and/or a safe pre-seed strategy based on keydat absence.
11. If keydat already exists, do not hang waiting for a prompt that will never appear.
12. Parse "writing :" as extract progress and "Packing:" as pack progress.
13. Support timeout, CancellationToken, process-tree termination on cancellation where possible, stdout/stderr capture, and structured errors.
14. Validate success using artifacts as well as exit code:
    - extract: expected extract folder exists and contains files;
    - first keydat-less run: expected generated keydat exists;
    - pack: resulting working archive exists and is non-zero.
15. Never mutate the pristine sample. Copy 015.ab into a per-test disposable workspace first.
16. Add Windows-only integration tests:
    A. extract with keydat missing -> automatically choose 1 -> keydat generated -> folder/files extracted -> writing progress observed.
    B. extract with keydat already present -> completes without country prompt deadlock.
    C. pack with keydat present -> Packing progress observed.
    D. pack with keydat intentionally missing -> automatically choose 1 -> keydat generated -> pack completes.
    E. working path contains spaces and Vietnamese Unicode.
17. Add unit tests using a fake child process or parser fixtures for:
    - Select: without newline;
    - writing progress;
    - Packing progress;
    - timeout;
    - cancellation;
    - invalid executable;
    - invalid/missing archive;
    - tool hash mismatch if integrity service already exists.
18. Keep UI completely out of this task.
19. Build and run all relevant tests.
20. At the end report the exact observed real ACV Tool 5 behavior and any discrepancy from this specification.

Security constraints:
- keydat is a runtime companion artifact, not a secret/DRM boundary.
- do not log sensitive full paths unnecessarily.
- use absolute acv.exe path and controlled working directory.
- do not run arbitrary executable paths supplied by project content.
- preserve pristine template hash.
```

---

# 11. SECURITY REALITY CHECK FOR PRODUCT OWNER

1. **Không có Windows client nào “không thể crack”.** Obfuscation, Native AOT, anti-tamper và encryption chỉ tăng chi phí reverse engineering.
2. **Không đặt bí mật kinh tế trên client.** AI keys, privileged DB keys, credits và payment verification phải nằm server-side.
3. **Nếu một user phải nhận được một archive hoàn chỉnh để copy vào Audition, họ có khả năng sao chép/archive-extract file đó.** Không được marketing hoặc thiết kế với giả định raw game content sẽ tuyệt đối bí mật trên máy user.
4. Để giảm lộ pristine template: không bundle raw template trong installer, kiểm soát download bằng entitlement, mã hóa cache at-rest, giải mã just-in-time, cleanup workspace, version/hash/sign package.
5. Nếu bảo mật pristine template quan trọng hơn tốc độ/offline, đánh giá server-side build; nhưng final archive vẫn có thể chứa các asset có thể được extract.
6. App chạy Administrator toàn bộ theo yêu cầu hiện tại làm tăng blast radius khi có bug/malicious input. Giữ kiến trúc đủ sạch để tương lai có thể tách `normal UI + elevated broker` mà không rewrite core.
7. Trước khi bán công khai, xác minh quyền phân phối `acv.exe`, archive/template và game assets.

---

# END OF MASTER ROADMAP V3
