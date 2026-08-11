# Trạng thái roadmap

Nguồn đặc tả duy nhất cho thứ tự và acceptance criteria là `Audition_AI_Mod_Studio_MASTER_ROADMAP_V3.md`. Không sao chép hoặc làm yếu acceptance criteria tại đây.

## Trạng thái hiện tại

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
- PLAN kế tiếp theo đặc tả gốc: PLAN 33 — Load/Recover Project; không tự động bắt đầu.

## Stage Gates

- Gate A: real archive extract và interactive AuditionVN selection.
- Gate B: real DDS roundtrip dựa trên metadata của chính target DDS; có thể dùng tập optional sample đa dạng, không phụ thuộc bắt buộc vào `tn_coby_logo.dds`.
- Gate C: replace, pack và kiểm thử trong Audition thật.

Không phát triển AI/store trước khi Gate A đạt. Archive/DDS pipeline chỉ được xem là đã chứng minh sau Gate C.

## Quy trình

Mỗi lần chỉ thực hiện một PLAN:

`PLAN → BUILD → TEST → BÁO CÁO → DỪNG`
