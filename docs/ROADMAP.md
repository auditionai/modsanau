# Trạng thái roadmap

Nguồn đặc tả duy nhất cho thứ tự và acceptance criteria là `Audition_AI_Mod_Studio_MASTER_ROADMAP_V3.md`. Không sao chép hoặc làm yếu acceptance criteria tại đây.

`PRODUCT DIRECTION LOCK — FILE-ONLY ARCHIVE EDITOR` gần đầu master roadmap thắng mọi wording lịch sử/future
xung đột. Audit tại `docs/ROADMAP_DIRECTION_AUDIT.md` đã review 40 PLAN cũ 61–100, thêm PLAN 75 — Privilege
Model Review và renumber phần sau thành PLAN 76–101. Không có PLAN game install/launch/runtime nào được giữ lại.

## Trạng thái hiện tại

- PLAN đã hoàn thành: PLAN 66 — Prompt Presets.
- Phạm vi PLAN 66: preset provider-neutral có immutable ID/version, operation và exact `(GameId, ModId, texture semantic type)`; local user store schema-v1 atomic với strict/bounded import/export; deterministic local/cloud merge cô lập conflict; cloud fail-closed/offline local fallback; selector trong AI Studio chỉ điền prompt và không submit job, reserve credit, sửa mask hay Apply.
- PLAN đã hoàn thành: PLAN 67 — Threat Model.
- Phạm vi PLAN 67: threat model cho kiến trúc PLAN 01–66 với assets/attackers, trust và data-flow diagrams, ranked abuse cases, owner/mitigation/test mapping, control status và explicit residual risk; không coi game process/install/runtime là application workflow.
- PLAN đã hoàn thành: PLAN 68 — Secret Separation.
- Phạm vi PLAN 68: scanner CI cho source/build/log/crash artifacts, negative tests trên từng artifact class và rotation/incident runbook; privileged credentials vẫn chỉ thuộc deployed secret store, không chuyển sang obfuscation/hidden client configuration.
- PLAN đã hoàn thành: PLAN 69 — Secure Local Session Storage Audit.
- Phạm vi PLAN 69: harden store PLAN 58 bằng schema-v1 strict, in-place legacy Credential Manager migration, process-wide target serialization, bounded token/blob và buffer zeroing; test rotation/delete/corruption/migration/concurrency và release plaintext scan, không tạo store thứ hai.
- PLAN đã hoàn thành: PLAN 70 — Server-Authoritative License/Entitlement.
- Phạm vi PLAN 70: authenticated server entitlement-record boundary và short-lived ES256 grants bind verified user/scope/audience/template/game/mod/expiry/nonce; atomic nonce consume chống replay, premium AI offline fail closed, không local `IsPremium`, không template distribution hoặc game-install coupling.
- PLAN đã hoàn thành: PLAN 71 — Premium Template Distribution Model.
- Phạm vi PLAN 71: server-controlled premium package catalog, signed manifest bind exact template/version/hash/game/mod, authenticated short-lived private-storage access reuse PLAN 70 grant/nonce, revocation/fail-closed policy và non-destructive existing-project semantics. Concrete storage/download/cache adapter chưa được cấu hình; PLAN 72 sở hữu encrypted local cache.
- PLAN đã hoàn thành: PLAN 72 — Encrypted Local Template Cache.
- Phạm vi PLAN 72: exact-identity encrypted cache dùng per-entry random AES-256-GCM key, Windows DPAPI current-user wrapping, chunked bounded I/O, atomic write/materialization, corruption/key-loss cleanup và per-entry concurrency serialization. Cache không phải entitlement/DRM authority và không chứa game-install path.
- PLAN đã hoàn thành: PLAN 73 — Template Exposure & Build-Location Decision.
- Quyết định ADR-0001: hybrid — server-controlled acquisition + encrypted local cache + client-side local build/export. Server worker không được chọn cho V1; authorized user vẫn có thể recover final archive, nên legal evidence và residual-risk disclosure là release gate.
- PLAN đã hoàn thành: PLAN 74 — Protected Workspace Hardening.
- Phạm vi PLAN 74: random managed workspace được harden bằng exact protected NTFS DACL owner/SYSTEM/Administrators, reparse/read-back gates, fail-safe partial cleanup và existing crash/concurrency ownership semantics; không plaintext global cache, raw-key logging hoặc secure-delete claim.
- PLAN đã hoàn thành: PLAN 75 — Privilege Model Review.
- ADR-0002/PLAN 90: runtime file-only dùng unelevated `asInvoker`; broker chỉ khi có privileged operation thật với closed
  protocol/path allowlist. Không implement broker/UAC trong runtime.
- PLAN đã hoàn thành: PLAN 76 — App Code Signing.
- Phạm vi PLAN 76: protected release signing workflow và fail-closed Authenticode/timestamp verification cho explicit
  app-owned EXE/DLL/package/installer artifact; private key chỉ ở certificate store/HSM/service ngoài repository.
  Debug vẫn unsigned, installer production chưa tồn tại, production certificate/timestamp/live signing chưa VERIFIED.
- PLAN đã hoàn thành: PLAN 77 — App Update Signing & Verification.
- Phạm vi PLAN 77: strict signed ES256 update manifest bind exact HTTPS URL/version/length/hash/publisher; typed downgrade
  rejection; randomized bounded staging; WinVerifyTrust + exact Authenticode identity; installer boundary chỉ chạy sau
  verify. Concrete install/swap/rollback/UI thuộc PLAN 96; live manifest signer/network/installer chưa VERIFIED.
- PLAN đã hoàn thành evaluation: PLAN 78 — Binary Obfuscation Strategy.
- Quyết định ADR-0003: Dotfuscator Professional 7.2.2 là candidate pilot, nhưng chưa adopt vì thiếu license/tool và
  WinUI/XAML/reflection/native/AV evidence. Debug/Release hiện không obfuscate; anti-tamper disabled; mapping phải private.
- PLAN đã hoàn thành evaluation: PLAN 79 — Native AOT / Native Core Evaluation.
- Phạm vi PLAN 79: chạy Release x64 Native AOT probe thật; ghi nhận `IL2026`/`IL3050` serializer blockers và ma trận
  WinUI/DirectXTex/Supabase/serializer/native interop/plugin. Quyết định `NOT ADOPTED / PRODUCTION NOT VERIFIED`.
- PLAN đã hoàn thành contract: PLAN 80 — Client Integrity / Anti-Tamper Checks.
- Phạm vi PLAN 80: reusable PLAN 77 Authenticode adapter; manifest hash-before-parse; strict normalized resource/companion
  length+SHA-256 inventory; tamper chuyển risky capabilities về diagnostics-only. Không anti-debug/malware-like response;
  concrete signed release binding và UI enforcement vẫn `PRODUCTION NOT VERIFIED`.
- PLAN đã hoàn thành: PLAN 81 — DLL Hijacking / Process Launch Hardening.
- Phạm vi PLAN 81: absolute exact verified ACV/DirectXTex paths; app/child DLL search bỏ cwd/user dirs; system-only child
  `PATH`; minimal no-secret environment; standalone companion inventory và DirectXTex re-hash sát launch. Không shell,
  sandbox/anti-debug, privilege change hay game process behavior; signed packaging/native inventory chưa production-verified.
- PLAN đã hoàn thành: PLAN 82 — API Replay / Abuse Protection.
- Phạm vi PLAN 82: HTTPS fail-closed không redirect; trusted-proxy allowlist; per-IP pre-auth và per-verified-user rate
  limiting không queue; Supabase JWT lifetime/sub binding; exact JSON/media/body bounds; durable charged-operation
  idempotency/ownership reuse; structured redacted audit. Distributed edge limit, SIEM và live TLS/Supabase deployment vẫn
  `PRODUCTION NOT VERIFIED`.
- PLAN đã hoàn thành: PLAN 83 — Supabase RLS Hardening.
- Phạm vi PLAN 83: audit 6 user-owned tables trong existing `private` schema; FORCE RLS; five own-row SELECT policies và
  safe column grants; idempotency/internal columns server-only; no client DML/RPC; cross-user gate PASS trên PostgreSQL
  17.6 pinned digest. Supabase staging/live chưa verified; PLAN 60 function ambiguity được ghi nhận cho exact PLAN 85.
- PLAN đã hoàn thành contract: PLAN 84 — Payment Security.
- Phạm vi PLAN 84: Stripe raw-body HMAC/timestamp verification, exact live/test + server product/amount/currency catalog,
  bounded anonymous webhook ingress và append-only dual event/payment idempotency trước exact PLAN 60 `credit_grant`.
  Client success state không có authority; live Stripe chưa verified; PLAN 85 đã xử lý lỗi PLAN 60 đã biết và local
  PostgreSQL payment fulfillment đã PASS.
- PLAN đã hoàn thành gate: PLAN 85 — Credit Concurrency Integration Gate.
- Phạm vi PLAN 85: sửa tại chỗ ambiguity `42702` trong năm PLAN 60 functions và chứng minh real PostgreSQL lifecycle,
  overspend race, duplicate/conflicting key, capture-vs-release, over-refund và PLAN 84 payment-to-grant idempotency.
  Không tạo credit schema/service thứ hai; Supabase staging/live vẫn `PRODUCTION NOT VERIFIED`.

- PLAN đã hoàn thành: PLAN 01 — Repository & Solution Foundation.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 02 — Windows App Bootstrap + Administrator.
- Phạm vi PLAN 02: Generic Host/DI lifecycle, LocalAppData paths, file logging, startup validation, global exception handling, graceful shutdown và UAC manifest.
- Ngoài phạm vi: settings, fixture registration, ACV, DDS, Imaging, Mods, Projects, AI/Cloud và các security feature của PLAN sau.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 03 — App Paths & Secure Workspace Paths.
- Phạm vi PLAN 03: centralized managed directories, canonical relative-path validation, randomized isolated workspace, exclusive active marker, abandoned cleanup và reparse-point mitigation.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 04 — Settings System.
- Phạm vi PLAN 04: strongly typed non-secret JSON settings, schema v1, validation, atomic save, bounded backup, corruption recovery, concurrency và cancellation.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 05 — Real Sample Fixture Registration.
- Phạm vi PLAN 05: private fixture catalog, optional DDS sample metadata, canonical source location và copy-before-mutation trong isolated workspace. `tn_coby_logo.dds` không phải fixture bắt buộc; source of truth DDS là các file được scan từ working archive đã extract.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 06 — ACV Tool 5 Interactive Process Runner.
- Phạm vi PLAN 06: direct child process, redirected stdin/stdout/stderr, chunk parser, trusted AuditionVN profile, deterministic state/progress, timeout/cancellation, process-tree termination và artifact verification bằng fake process.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 07 — Keydat Lifecycle + ACV Tool Integrity.
- Phạm vi PLAN 07: centralized keydat derivation/status/copy/lifecycle; code-owned ACV Tool manifest; source/copy/pre-launch SHA-256 verification; structured integrity rejection và TOCTOU documentation.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 08 — Audition Archive Abstraction.
- Phạm vi PLAN 08: extension-independent archive descriptor; semantic extract/pack contract; explicit engine resolution; pristine-to-working verified copy; secure extracted path; ACV Tool 5 provisioning/keydat/runner orchestration; structured progress/error/cancellation/timeout.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 09 — Project Archive Workspace.
- Phạm vi PLAN 09: per-project randomized secure workspace lease; verified pristine-to-working copy; separate extracted/build output; template identity/version snapshot; atomic schema-v1 manifest; structured validation và isolated cleanup.
- PLAN đã hoàn thành kiểm chứng Gate A: PLAN 10 — Real Extract Integration POC With AuditionVN Selection.
- Phạm vi PLAN 10: real approved `acv.exe` + working copy `015.ab`; AuditionVN stdin automation; missing/existing-`PresentUnverified` keydat flows; bounded real stdout diagnostics; Unicode/space workspace; aggregate extract inventory; pristine hash verification và cleanup.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 11 — Recursive Asset Scanner.
- Phạm vi PLAN 11: catalog deterministic từ extracted directory của project workspace; relative-path identity; DDS/PNG/SLK/RGM/Other classification; streaming SHA-256; inventory; progress/cancellation; path confinement; reparse-point và duplicate rejection.
- PLAN đã hoàn thành kiểm chứng Stage Gate archive round-trip: PLAN 12 — Real Archive Repack POC + Keydat Cases.
- Phạm vi PLAN 12: no-edit extract/pack/re-extract thật; existing/missing-keydat; trusted AuditionVN selection; real pack exit-code convention; Unicode/space workspace; logical asset identity/size/kind/SHA-256 preservation và pristine fixture safety.
- PLAN đã hoàn thành kiểm chứng: PLAN 13 — DDS Metadata Reader.
- Phạm vi PLAN 13: contract/model strongly typed trong Core; parser legacy/DX10 little-endian read-only; FourCC/DXGI mapping; dimensions NPOT; mip/alpha/color-space semantics; structured malformed/unknown handling; synthetic tests và real extracted DDS inventory gate.
- PLAN đã hoàn thành evaluation: PLAN 14 — DirectXTex Evaluation Harness.
- Phạm vi PLAN 14: controlled `texconv` prototype pin version/hash; DDS→PNG; PNG→BC3; exact NPOT/unusual dimensions; forced mip count; forced legacy DXT5 header; cancellation/progress/security policy; real disposable-workspace evaluation và documented native API findings.
- PLAN đã hoàn thành: PLAN 15 — DDS Preview Service.
- Phạm vi PLAN 15: metadata preflight; controlled hash-pinned DirectXTex decode mip 0; immutable PNG-memory preview; alpha và RGBA/BGRA channel correctness; centralized resource policy; structured failures; timeout/cancellation; concurrent temp isolation/cleanup; 52/52 real DDS production-service gate.
- PLAN đã hoàn thành: PLAN 16 — Image → DDS Encoder.
- Phạm vi PLAN 16: immutable internal RGBA8 input; explicit target format/dimensions/mips/header/color/alpha settings; controlled DirectXTex BC1/BC3/RGBA8/BGRA8 encode; legacy/DX10 handling; validate-before-atomic-promote; preview roundtrip và real target-profile coverage.
- PLAN đã hoàn thành: PLAN 17 — Match Original DDS.
- Phạm vi PLAN 17: centralized metadata→strict profile; exact format/header/dimensions/effective-mips/color/resource semantics; BC1 alpha compatibility; `IDdsEncoder` orchestration; structured match report; 52/52 real profile audit và real representative matched encode without target/archive mutation.
- PLAN đã hoàn thành: PLAN 18 — DDS Validation.
- Phạm vi PLAN 18: independent `IDdsValidationService`; reopen target/candidate; exact structural metadata report; structured unsupported/missing/invalid/mismatch/cancelled failures; Match Original post-encode gate reuse; read-only workspace safety và không replacement.
- PLAN đã hoàn thành Gate B: PLAN 19 — Real DDS Roundtrip Gate.
- Phạm vi PLAN 19: đúng `tn_coby_logo.dds` từ disposable extract; Match Original → encoder → validation reopen → decode-back; exact BC3/DXT5 legacy 6000×1801/1 mip và source/archive hash bất biến.
- PLAN đã hoàn thành: PLAN 20 — Internal Image Model & Import.
- Phạm vi PLAN 20: PNG/JPEG/WebP/BMP signature-based import; metadata preflight; immutable packed straight-alpha RGBA8; EXIF orientation normalization; sRGB decode; structured failures; cancellation/concurrency; source read-only và DDS encoder interoperability.
- PLAN đã hoàn thành: PLAN 21 — Arbitrary Resize Engine.
- Phạm vi PLAN 21: `InternalImage` in/out; arbitrary NPOT dimensions; Stretch/Fit/Fill/Keep Aspect/Free Aspect/Manual Crop/Canvas Resize/Transparent Padding; center/top/bottom/left/right alignment; explicit nearest/linear sampling; premultiplied-alpha filtering; centralized resource limits và direct pixel backend không filesystem.
- PLAN đã hoàn thành: PLAN 22 — Interactive Crop/Transform Model.
- Phạm vi PLAN 22: immutable normalized crop state; custom aspect/anchor/minimum constraints; image-pixel/normalized/viewport coordinate mapping; fit-letterbox projection; zoom/pan/scale/quarter-turn rotate/flip/translate/reset; deterministic pixel crop và translation sang PLAN 21 Manual Crop without pixel copy.
- PLAN đã hoàn thành: PLAN 23 — Image Adjustments Core.
- Phạm vi PLAN 23: managed deterministic `InternalImage` adjustment pipeline cho brightness/contrast/exposure/saturation/vibrance/hue/temperature/tint/highlights/shadows/gamma/sharpen/blur/opacity; explicit range/order/sRGB semantics; exact alpha policy; cancellation/concurrency/resource validation và DDS RGBA interoperability.
- PLAN đã hoàn thành: PLAN 24 — Edit History / Undo Redo.
- Phạm vi PLAN 24: UI-independent per-editor history session; hybrid immutable state/image-reference snapshots; linear undo/redo và redo invalidation; stable revisions, saved checkpoint/dirty state; transaction coalescing; bounded entry/byte budget, unique image accounting và oldest-undo eviction.
- PLAN đã hoàn thành: PLAN 25 — Alpha Channel Utilities.
- Phạm vi PLAN 25: `InternalImage` in/out cho view/invert/threshold; immutable tightly-packed `AlphaChannelData` cho extract/replace; grayscale opaque view; exact `A >= threshold`; byte-exact RGB và hidden-RGB preservation; structured validation, cancellation, concurrency, resource policy, history và DDS interoperability.
- PLAN đã hoàn thành: PLAN 26 — Game Catalog.
- Phạm vi PLAN 26: strongly typed stable `GameId`; immutable `GameDefinition`; code-owned deterministic `IGameCatalog`; built-in Audition entry; structured duplicate/null/empty validation; Try lookup; singleton DI và không chứa archive filename/path/engine/region metadata của PLAN 27.
- PLAN đã hoàn thành: PLAN 27 — Mod Definition.
- Phạm vi PLAN 27: strongly typed `ModId`; immutable `ModDefinition`; explicit `GameId`; semantic display/category/cover/description; reuse versioned `AuditionArchiveTemplate`; explicit engine/region/keydat/install/compatibility mapping; deterministic game-scoped `IModCatalog`; atomic structured validation và singleton DI. Không tạo built-in Mod Type vì roadmap chưa cung cấp đủ semantic metadata có thẩm quyền.
- PLAN đã hoàn thành: PLAN 28 — Texture Manifest.
- Phạm vi PLAN 28: immutable manifest gắn explicit với Game/Mod; semantic texture slot ID; normalized relative DDS identity; friendly display/category/description/tags/preview/editable/recommended-edit metadata; atomic duplicate/collision validation; code-owned catalog và raw filename/path fallback khi mapping thiếu. Không có built-in production mapping khi chưa có authoritative metadata.
- PLAN đã hoàn thành: PLAN 29 — Smart Mod Scan.
- Phạm vi PLAN 29: reuse recursive archive scanner; validate Game/Mod identity; enrich observed DDS bằng real `IDdsMetadataReader`; generate bounded thumbnails qua preview + in-memory import + resize; exact manifest mapping; raw unknown fallback; missing-slot reporting; deterministic folder grouping; cancellation/progress và atomic structured failure. Real `015.ab` gate xác minh 101 files/52 DDS/46 PNG/3 RGM mà không sửa source.
- PLAN đã hoàn thành: PLAN 30 — Template Versioning.
- Phạm vi PLAN 30: canonical typed identity `templateId + version + SHA256 + compatible game build`; immutable multi-version catalog với exact lookup và explicit current marker; atomic conflict validation; exact project snapshot; structured exact/current-differs/missing/hash/build/legacy-invalid resolution; tuyệt đối không silent migration. Production catalog rỗng vì chưa có authoritative template metadata.
- PLAN đã hoàn thành: PLAN 31 — Project Model `.audproj`.
- Phạm vi PLAN 31: immutable schema-v1 aggregate lưu project/game/mod/exact-template identity, logical workspace references, edited textures, image/AI assets, edit/history references, build state và timestamps; atomic graph validation, deterministic ordering và generic content SHA-256. Chưa triển khai filesystem persistence/workflow/recovery.
- PLAN đã hoàn thành: PLAN 32 — Create Project Workflow.
- Phạm vi PLAN 32: typed Game/Mod/Name orchestration; fail-closed entitlement/template acquisition; trusted region resolution; verified project workspace; archive extract với existing keydat policy; Smart Scan/manifest/DDS metadata cache; atomic `.audproj` save; cancellation/progress và full rollback partial state.
- PLAN đã hoàn thành: PLAN 33 — Load/Recover Project.
- Phạm vi PLAN 33: strict `.audproj` load; exact template version/hash/build binding; secure retained-workspace reopen; manifest/archive validation; cache validation/rebuild; chỉ re-extract khi workspace thiếu hoặc không hợp lệ; typed progress/failure và atomic save sau recovery.
- PLAN đã hoàn thành: PLAN 34 — Texture State Machine.
- Phạm vi PLAN 34: pure immutable state evaluation cho `Original`, `Modified`, `AiGenerated`, `Pending`, `Invalid`, `Missing`; reuse project edited/image/AI asset references và explicit runtime observation; deterministic priority, typed failure và transition reporting, không filesystem mutation.
- PLAN đã hoàn thành: PLAN 35 — Reset Texture / Reset Project.
- Phạm vi PLAN 35: transactional single-texture restore từ regenerated exact-template workspace; project model/edit/history/cache update; full-project fresh working-copy swap; rollback trước commit; explicit post-commit cache/cleanup recovery flags; global pristine template không bị mutate.
- PLAN đã hoàn thành: PLAN 36 — Thumbnail Cache.
- Phạm vi PLAN 36: thumbnail được định danh bằng SHA-256 nội dung nguồn + kích thước yêu cầu + schema cache; cache hai tầng memory/disk có giới hạn; async single-flight theo key; ghi disk atomic; entry thiếu/hỏng được tạo lại qua pipeline preview/import/resize hiện có.
- PLAN đã hoàn thành: PLAN 37 — Lazy Loading.
- Phạm vi PLAN 37: Smart Scan phát hành metadata-only texture catalog; thumbnail được yêu cầu riêng qua cache PLAN 36; full immutable texture chỉ decode/import qua explicit selected-texture API và không được service giữ lại.
- PLAN đã hoàn thành: PLAN 38 — Background Task Manager.
- Phạm vi PLAN 38: bounded central channel cho Extract/Scan/Thumbnail/Resize/Convert/Build/Download/AI; configurable workers; immutable queued/running/progress/terminal snapshots; cancel queued/running; notifications; structured failure và bounded in-memory history.
- PLAN đã hoàn thành: PLAN 39 — Temp Cleanup & Crash Recovery.
- Phạm vi PLAN 39: versioned lock/session metadata cho managed workspace; startup discovery không phá hủy; typed Active/StaleRecoverable/StaleCleanupOnly/Unsafe inventory; explicit exact-ID recovery hoặc cleanup với lock/reparse revalidation tại thời điểm action.
- PLAN đã hoàn thành: PLAN 40 — Design System.
- Phạm vi PLAN 40: WinUI three-layer design tokens; dark/light/high-contrast semantic contract; shared acrylic panel, rounded/gradient cards, depth hover/pressed buttons, badge và typography styles; keyboard focus/accessibility/performance rules.
- PLAN đã hoàn thành kiểm chứng: PLAN 41 — App Shell.
- Phạm vi PLAN 41: shell WinUI thích ứng với 8 route strongly typed; top bar account/credits/notifications/connection; ViewModel presentation state; keyboard focus sau navigation; reuse design system Default/Light/HighContrast. Nội dung feature vẫn là placeholder theo đúng ranh giới PLAN.
- PLAN đã hoàn thành kiểm chứng: PLAN 42 — Home: Game → Mod First.
- Phạm vi PLAN 42: Home theo thứ tự Game → compatible Mod Type → project name → Create; catalog typed; explicit empty state khi production Mod Catalog chưa có metadata có thẩm quyền; create qua Background Task Manager và existing Project Creation Service; active project session giữ retained workspace lease.
- PLAN đã hoàn thành kiểm chứng: PLAN 43 — Project Workspace UI.
- Phạm vi PLAN 43: route Projects với folder tree/search/mapping filter, preview/editor surface, metadata/edit-options pane và status target size/format/state/validation; metadata scan qua Background Task Manager + Smart Scan; state qua Texture State Machine; explicit no-project/loading/error states.
- PLAN đã hoàn thành kiểm chứng: PLAN 44 — Texture Grid + Search.
- Phạm vi PLAN 44: card texture có lazy thumbnail, friendly/raw filename, size và textual state; tìm kiếm kết hợp mapping/status/category/size/alpha facets; thumbnail chỉ tải cho card được hiện thực hóa qua Background Task Manager + Lazy Loading Service.
- PLAN đã hoàn thành kiểm chứng: PLAN 45 — Crop/Resize Canvas UI.
- Phạm vi PLAN 45: route Image Editor tải full texture theo yêu cầu qua Lazy Loading + Background Task Manager; canvas target frame exact DDS dimensions; zoom/pan/crop và typed modes Crop/Fit/Fill/Stretch/Canvas/Padding reuse geometry/resize contracts PLAN 21–22; toàn bộ state vẫn preview-only.
- PLAN đã hoàn thành kiểm chứng: PLAN 46 — Before/After Compare.
- Phạm vi PLAN 46: Before là immutable working-copy baseline của editor session; After là live non-destructive resize/crop preview có cancellation và generation guard; side-by-side/slider/toggle dùng chung compare camera; checkerboard chỉ là presentation; bitmap được reuse giữa mode và compare không mutate project/history/DDS/archive.
- PLAN đã hoàn thành kiểm chứng: PLAN 47 — Apply Texture UX.
- Phạm vi PLAN 47: validate target; render typed crop/resize; temporary Match Original encode + independent validation; atomic extracted-workspace replacement có rollback; durable before/after history assets; Modified/build-dirty state; content-hash thumbnail regeneration; atomic project save; UI progress/cancel qua Background Task Manager.
- PLAN đã hoàn thành kiểm chứng: PLAN 48 — Project Validator.
- PLAN đã hoàn thành kiểm chứng: PLAN 49 — Build Pipeline.
- PLAN đã hoàn thành Product Gate C: PLAN 50 — Real Replace + Pack Gate.
- Product direction hậu PLAN 50 xác nhận ứng dụng là file editor/archive builder; mọi future game
  install/detect/backup/restore/launch direction bị supersede.
- PLAN đã hoàn thành kiểm chứng: PLAN 51 — Export Destination.
- Phạm vi PLAN 51: typed read-only validation cho arbitrary user-selected export directory/filename; canonical
  absolute path, Unicode/spaces, trusted extension contract, access/reparse/collision semantics và explicit
  overwrite intent. Không persistence/schema change, không copy archive và không game-install metadata.
- PLAN đã hoàn thành kiểm chứng: PLAN 52 — Atomic Archive Export.
- Phạm vi PLAN 52: consume exact validated PLAN 49 build artifact; source re-hash; destination revalidation;
  durable temp copy; size/SHA-256 verification; atomic promotion; explicit overwrite backup/rollback và typed
  rollback failure. Không rebuild, project mutation hoặc game filesystem interaction.
- PLAN đã hoàn thành kiểm chứng: PLAN 53 — Build & Export UI.
- Phạm vi PLAN 53: workflow file-only trong Project Workspace cho chọn folder/tên archive, explicit replace,
  Validate → Build → Export qua application services và một Background Task Manager job; progress/cancel,
  structured safe status, final path/size/SHA-256. Không copy trong code-behind, không game path/install/launch.
- PLAN đã hoàn thành kiểm chứng: PLAN 54 — Batch Build & Export.
- Phạm vi PLAN 54: typed multi-project jobs `Queued → Validating → Building → Exporting → terminal`; deterministic
  project-ID filename + trusted extension, full destination preflight, explicit duplicate conflict policy,
  bounded concurrency qua PLAN 38 và per-job failure/cancellation isolation. Không batch game install.
- PLAN đã hoàn thành kiểm chứng: PLAN 55 — File-Only Production Gate.
- Phạm vi PLAN 55: production file-only chain từ pristine fixture → packed template → Create Project → scan →
  Apply → Project Validate → Build → Export → canonical staging/re-extract; 320/320 asset, đúng một target đổi,
  319 non-target byte-identical, pristine safety và final artifact size/SHA-256. Không kiểm thử hoặc tương tác game.
- Batch PLAN 51–55 đã hoàn thành; không tự động triển khai PLAN tiếp theo.
- PLAN đã hoàn thành kiểm chứng: PLAN 56 — Batch Build Summary.
- Phạm vi PLAN 56: summary typed/deterministic cho `Changed/Failed/Skipped`; exact `Changed` set phải khớp
  `AuditionProject.EditedTextures`, và đúng một PLAN 49 build được gọi khi có approved changes. Không tạo batch
  Apply/build path thứ hai, không export và không chạm game.
- PLAN đã hoàn thành kiểm chứng: PLAN 57 — AI Provider Abstraction.
- Phạm vi PLAN 57: `IAiService` async typed cho Generate/Edit/Inpaint/Outpaint/RemoveObject/ReplaceObject/Upscale,
  reuse `InternalImage`, bounded prompt/target size và typed progress/result/cancellation. Desktop DI mặc định dùng
  fail-closed `UnavailableAiService`; không có provider credential, endpoint hoặc network call.
- PLAN đã hoàn thành kiểm chứng: PLAN 58 — Supabase Auth.
- Phạm vi PLAN 58: contract auth typed cho sign up/sign in/sign out/session refresh/password reset/profile;
  Supabase Auth transport chỉ nhận HTTPS project URL + publishable key, refresh được serialize và token rotation
  được ghi lại qua `ISecureSessionStore`. Desktop dùng Windows Credential Manager, không lưu session secret trong
  JSON/settings và fail closed khi chưa có cấu hình. Không có gateway/provider credential/credit logic.
- PLAN đã hoàn thành kiểm chứng: PLAN 59 — Backend Trusted Gateway.
- Phạm vi PLAN 59: ASP.NET Core server host độc lập với authenticated endpoints cho bảy AI operation, credit
  snapshot read và exact template entitlement. Gateway introspect bearer token qua Supabase Auth, lấy UserId duy
  nhất từ verified principal, validate request/response bounded và giữ provider configuration phía server. Trusted
  capability mặc định fail closed; chưa có wallet/ledger/reservation/refund hoặc client-supplied commercial state.
- PLAN đã hoàn thành kiểm chứng: PLAN 60 — Credit Ledger.
- Phạm vi PLAN 60: PostgreSQL/Supabase private schema cho wallet projection, reservation state machine,
  append-only ledger/refund/idempotency records và năm transactional server functions `grant/reserve/capture/release/refund`.
  Gateway dùng pooled Npgsql data source với TLS, parameterized commands, explicit transaction/cancellation và fail-closed
  khi database chưa cấu hình. HTTP vẫn chỉ cho đọc snapshot; không có client mutation hoặc client-supplied balance/cost/refund/payment state.
- PLAN đã hoàn thành kiểm chứng: PLAN 61 — AI Pricing.
- Phạm vi PLAN 61: immutable server-hosted catalog có version/effective time và integer credit price cho đủ bảy AI
  operations; authenticated bounded quote endpoint trả display estimate, stale version trả 409 kèm current quote,
  invalid/unavailable catalog fail closed. Client không gửi cost/provider và quote không reserve/charge ledger.
- PLAN đã hoàn thành kiểm chứng: PLAN 62 — AI Job System.
- Phạm vi PLAN 62: private PostgreSQL durable job state machine, atomic enqueue/reserve, owner-scoped history/cancel,
  idempotent transition functions, worker leases/retry recovery và capture/release lifecycle dùng lại PLAN 60.
  Gateway không nhận client charge, không persist raw image/secret và fail closed khi DB chưa cấu hình.
- PLAN đã hoàn thành kiểm chứng: PLAN 63 — AI Studio UI.
- Phạm vi PLAN 63: accessible adaptive WinUI/MVVM form, server quote/history/cancel adapter, PLAN 38 background execution,
  bounded inputs và cancellable `InternalImage` preview. Không Apply/project/DDS mutation; offline backend không ảnh hưởng
  local editing/build/export.
- PLAN đã hoàn thành kiểm chứng: PLAN 64 — AI Mask Editor.
- Phạm vi PLAN 64: mask bất biến theo đúng tọa độ/kích thước ảnh nguồn, brush/erase deterministic, hardness/opacity,
  clear/invert/show-hide, zoom/pan đồng bộ, undo/redo có giới hạn bộ nhớ và composition có thể hủy. Mask chỉ được lưu
  khi người dùng yêu cầu, bằng atomic replacement trong project workspace; không Apply/DDS, gọi provider hay chạm game.
- PLAN đã hoàn thành kiểm chứng: PLAN 65 — Inpaint / Outpaint / Remove / Replace / Upscale.
- Phạm vi PLAN 65: năm semantic operation dùng authenticated durable job, opaque private content, trusted
  provider/profile mapping, lease worker và server credit lifecycle; ambiguous outcome giữ reservation ở
  `ReconciliationRequired`. Desktop chỉ tải output bounded thành `InternalImage` preview; explicit approval mới reuse
  Match Original/DDS validation/atomic Apply/history. Concrete external provider và Supabase staging/live chưa kiểm chứng;
  local PostgreSQL credit concurrency đã PASS ở PLAN 85.
- PLAN kế tiếp: PLAN 66 — Prompt Presets.
- Milestone hiện tại: Milestone 4 — AI Workflow & Commercial Backend.

## Stage Gates

- Gate A: real archive extract và interactive AuditionVN selection.
- Gate B: real DDS roundtrip dựa trên metadata của chính target DDS; có thể dùng tập optional sample đa dạng, không phụ thuộc bắt buộc vào `tn_coby_logo.dds`.
- Gate C: replace, production pack/re-extract, intended/non-target integrity, pristine safety và artifact hash.

Manual in-game QA không thuộc product acceptance scope. Không phát triển AI/store trước khi Gate A đạt.
Archive/DDS pipeline chỉ được xem là đã chứng minh sau file-only Product Gate C.

## Quy trình

Mỗi lần chỉ thực hiện một PLAN:

`PLAN → BUILD → TEST → BÁO CÁO → DỪNG`

## Cập nhật batch PLAN 86–90

- PLAN 86 — Device Sessions / Abuse Controls: đã triển khai; Gateway feature-flagged registration/list/revoke, random client
  device identity trong Credential Manager, server session binding, configured device limit, throttled last-seen, private
  PostgreSQL/RLS và immediate cloud deny sau revoke. Local PostgreSQL gate PASS; Supabase live chưa xác minh.
- PLAN 87 — Privacy & Logging Security: đã triển khai central sink redaction cho desktop/Gateway và diagnostic ZIP allowlist
  chỉ gồm safe manifest + log đã redact; mặc định loại project, ảnh, template, archive, workspace, settings và credential.
- PLAN 88 — Dependency / Supply-Chain Security: exact NuGet lock graph, source/signature policy, vulnerability/SBOM CI gate,
  full-SHA Actions và native/license/redistribution inventory đã triển khai. ACV/template/game asset commercial redistribution
  vẫn fail-closed do thiếu authoritative written rights/provenance.
- PLAN 89 — Security Test Matrix: đã triển khai ma trận 12/12 có runner fail-closed; bổ sung real PostgreSQL duplicate-charge/
  cross-user ownership và real ACV malformed-archive evidence. Không thay local evidence thành production claim.
- PLAN 90 — Release Penetration / Crack-Resistance Review: internal manual review đủ 8/8; sửa app-wide elevation bằng
  `asInvoker`, loại public PDB/source/private artifact và tăng forged-role RLS evidence. Paid beta production vẫn NO-GO theo
  các blocker live/signing/redistribution/external sign-off đã ghi trong review.
- Batch PLAN 86–90 hoàn tất; dừng trước PLAN 91 — Remote Product Catalog (Game/Mod/Template Taxonomy).
  Xem [SECURITY_TEST_MATRIX.md](SECURITY_TEST_MATRIX.md) và
  [RELEASE_PENETRATION_REVIEW.md](RELEASE_PENETRATION_REVIEW.md).
