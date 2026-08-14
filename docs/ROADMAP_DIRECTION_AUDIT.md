# Roadmap Direction Audit — File-Only Product Scope Lock

## Audit basis

- Checkpoint được audit: `e24757a` sau khi PLAN 56–60 đã hoàn thành và commit.
- Historical PLAN 01–60 không bị đổi thành future work. Audit bao phủ **40 PLAN chưa triển khai trong roadmap
  trước sửa đổi: PLAN 61–100**.
- Classification đánh giá product direction, không đánh giá implementation readiness. Mọi PLAN vẫn phải qua
  FUTURE PLAN EXECUTION CONTRACT và approval riêng.
- Kết quả: **30 ALIGNED, 10 NEEDS REVISION, 0 REMOVE/REPLACE, 1 PLAN mới thực sự cần thêm**.

## Audit từng future PLAN

| PLAN cũ — exact existing name | Classification | Reason và correct product interpretation | Dependencies | Explicit non-scope | Exact revised name |
|---|---|---|---|---|---|
| 61 — AI Pricing | ALIGNED | Server pricing/actual cost authority phục vụ AI image workflow. | 57, 59, 60 | Client charge authority; jobs/UI | 61 — AI Pricing |
| 62 — AI Job System | ALIGNED | Durable AI jobs + credit lifecycle, output là file/image reference. | 57, 59–61 | Apply/DDS/UI; game process | 62 — AI Job System |
| 63 — AI Studio UI | ALIGNED | UI tạo preview `InternalImage`, không mutate project trước approval. | 38, 57–62 | Provider secret; silent Apply/install | 63 — AI Studio UI |
| 64 — AI Mask Editor | ALIGNED | Mask theo source-image coordinates trong editor. | 20, 24, 63 | Provider/job execution; DDS Apply | 64 — AI Mask Editor |
| 65 — Inpaint / Outpaint / Remove / Replace / Upscale | ALIGNED | AI result → preview → approval → existing Apply/DDS pipeline. | 47, 57, 62–64 | Bypass validator; auto-build/install | 65 — Inpaint / Outpaint / Remove / Replace / Upscale |
| 66 — Prompt Presets | ALIGNED | Versioned content metadata, local/cloud, không executable action. | 28, 57, 63 | Provider secret; automatic Apply | 66 — Prompt Presets |
| 67 — Threat Model | ALIGNED | Security documentation cho app/backend/file pipeline. | 01–66 | Anti-cheat/game process threat engineering | 67 — Threat Model |
| 68 — Secret Separation | ALIGNED | Enforce server-side privileged secrets. | 58–62, 67 | Obfuscation as secret boundary | 68 — Secret Separation |
| 69 — Secure Local Token Storage | NEEDS REVISION | PLAN 58 đã implement Credential Manager; cần audit/harden, không duplicate store. | 58, 67 | Store thứ hai; plaintext settings | 69 — Secure Local Session Storage Audit |
| 70 — Server-Authoritative License/Entitlement | ALIGNED | Commercial authority server-side, independent of game installation. | 58–59, 67 | Local premium boolean authority | 70 — Server-Authoritative License/Entitlement |
| 71 — Premium Template Distribution Model | ALIGNED | Authorized package download vào controlled cache/workspace. | 30, 59, 70 | Bundled raw premium archive; game discovery | 71 — Premium Template Distribution Model |
| 72 — Encrypted Local Template Cache | ALIGNED | Exposure-reduction cache, not DRM, decrypted only into workspace. | 03, 30, 71 | Game directory cache; secrecy promise | 72 — Encrypted Local Template Cache |
| 73 — Raw Template Exposure Analysis | NEEDS REVISION | “Playable archive” wording dễ kéo runtime assumption; cần ADR local/server/hybrid build-location. | 30, 49, 71–72 | Install/runtime validation | 73 — Template Exposure & Build-Location Decision |
| 74 — Protected Workspace Hardening | ALIGNED | Harden application-owned managed workspace. | 03, 09, 39, 67, 72 | Secure-delete promise; game folders | 74 — Protected Workspace Hardening |
| 75 — App Code Signing | ALIGNED | App distribution security; renumber vì privilege review phải đi trước release chain. | 67–74, new 75 | Game binary signing | 76 — App Code Signing |
| 76 — Update Signing & Verification | ALIGNED | Update chính Audition AI Mod Studio. | 76 revised | Game updater | 77 — App Update Signing & Verification |
| 77 — Binary Obfuscation Strategy | ALIGNED | Optional app hardening after compatibility tests. | 67–68, 76 | Secret/economic boundary | 78 — Binary Obfuscation Strategy |
| 78 — Native AOT / Native Core Evaluation | ALIGNED | Compatibility/performance evaluation, not DRM. | 76–78 revised | “Uncrackable” claim | 79 — Native AOT / Native Core Evaluation |
| 79 — Client Integrity / Anti-Tamper Checks | ALIGNED | Moderate app/tool/resource integrity. | 07, 76–79 revised | Aggressive anti-debug/game anti-cheat | 80 — Client Integrity / Anti-Tamper Checks |
| 80 — DLL Hijacking / Process Launch Hardening | ALIGNED | Harden app-owned native/tool process boundary. | 06–07, 67, 74 | Launch/inspect game process | 81 — DLL Hijacking / Process Launch Hardening |
| 81 — API Replay / Abuse Protection | ALIGNED | Protect trusted backend commercial calls. | 59–62, 67 | Client authority; game telemetry | 82 — API Replay / Abuse Protection |
| 82 — Supabase RLS Hardening | NEEDS REVISION | Must audit PLAN 60 schema/functions, not create parallel wallet model. | 58–60, 67, 82 revised | Direct client wallet mutation | 83 — Supabase RLS Hardening |
| 83 — Payment Security | ALIGNED | Verified server webhook is payment truth. | 60, 67, 82–83 revised | Client success proof | 84 — Payment Security |
| 84 — Credit Concurrency Security | NEEDS REVISION | PLAN 60 đã implement locking/idempotency; cần real PostgreSQL race gate. | 60, 62, 84 revised | Ledger/service thứ hai | 85 — Credit Concurrency Integration Gate |
| 85 — Device Sessions / Abuse Controls | ALIGNED | Optional backend account controls, không invasive game/DRM behavior. | 58–59, 67, 82 | Game process/hardware anti-cheat | 86 — Device Sessions / Abuse Controls |
| 86 — Privacy & Logging Security | ALIGNED | Redaction/diagnostic export boundary. | 67–68 | User assets included by default | 87 — Privacy & Logging Security |
| 87 — Dependency / Supply-Chain Security | ALIGNED | App/tool/package provenance and legal review. | 67, 76–81 revised | Game installation management | 88 — Dependency / Supply-Chain Security |
| 88 — Security Test Matrix | ALIGNED | Cross-boundary automated evidence. | 67–88 revised | Runtime/in-game acceptance | 89 — Security Test Matrix |
| 89 — Release Penetration / Crack-Resistance Review | ALIGNED | Review app/backend/template exposure; architectural fixes first. | 67–89 revised | Anti-cheat/hooking against game | 90 — Release Penetration / Crack-Resistance Review |
| 90 — Remote Game/Mod Catalog | NEEDS REVISION | “Game catalog” chỉ là semantic taxonomy; tên/scope phải cấm paths/discovery. | 26–30, 59, 70–71 | Registry/launcher/install metadata | 91 — Remote Product Catalog (Game/Mod/Template Taxonomy) |
| 91 — Template Admin Tool | NEEDS REVISION | Explicit admin-uploaded archive và isolated file validation; không source từ game install. | 11, 13, 28, 30, 71 | Game detection/mutation | 92 — File-Only Template Admin Tool |
| 92 — Account/Profile/Credit History UI | ALIGNED | Read-only server-sourced commercial UI. | 58, 60, 62 | Client balance mutation | 93 — Account/Profile/Credit History UI |
| 93 — Payment Abstraction | ALIGNED | Backend provider/webhook → PLAN 60 ledger. | 60, 84 revised | Client payment-success authority | 94 — Payment Abstraction |
| 94 — Installer | NEEDS REVISION | Must say installer của application itself, not game/mod installer. | 75 privilege review, 76–77 signing | Audition/mod install | 95 — Audition AI Mod Studio Application Installer |
| 95 — Updater | NEEDS REVISION | Must say updater của application itself. | 77, 95 revised | Game update/launch | 96 — Audition AI Mod Studio Application Updater |
| 96 — Archive Integration Test Suite | ALIGNED | File-only extract→scan→replace→pack→verify/export/re-extract. | 06–19, 47–55 | Game install/runtime | 97 — Archive File-Pipeline Integration Test Suite |
| 97 — DDS Compatibility Matrix | NEEDS REVISION | Compatibility authority là file metadata/roundtrip; in-game note remains optional external QA. | 13–19, 28–30 | Runtime acceptance gate | 98 — DDS File-Pipeline Compatibility Matrix |
| 98 — Crash/Recovery Tests | ALIGNED | Reliability của workspace/build/export/download transactions. | 32–39, 47–55, 62, 71–72 | Game backup/restore | 99 — Crash/Recovery Tests |
| 99 — Performance Tests | ALIGNED | File/image/archive/UI performance and memory. | Implemented local pipelines | Game FPS/runtime profiling | 100 — Performance Tests |
| 100 — V1 Release Gate | NEEDS REVISION | Gate phải explicit file-only, include privilege decision và app-vs-game distribution boundary. | Toàn bộ revised roadmap | In-game QA/game installation | 101 — V1 File-Only Product Release Gate |

## PLAN mới và dependency order

PLAN mới duy nhất: **PLAN 75 — Privilege Model Review**. PLAN này chỉ tạo ADR/inventory/test plan cho
`requireAdministrator`; không đổi manifest. Nó được đặt sau workspace security decision và trước signing/installer,
do đó PLAN cũ 75–100 được renumber thành 76–101. Không PLAN nào khác đổi thứ tự tương đối.

Dependency milestones sau audit:

1. Technical Proof — PLAN 01–19 (historical).
2. One Real Mod End-to-End — PLAN 20–35 + 48–50 (historical).
3. Editor Completion — PLAN 36–56 (historical).
4. AI Workflow & Commercial Backend — PLAN 57–66 + core security PLAN 67–85.
5. Application Hardening, Distribution & Public Paid Beta — PLAN 86–101.

## Legacy-direction findings

- `install relative path` của historical PLAN 27 và legacy settings fields chỉ được giữ để phản ánh lịch sử/schema;
  chúng không cấp game-install authority. Migration/removal phải có settings/domain migration PLAN riêng.
- Historical wording từng đề cập test trong game được PRODUCT DIRECTION LOCK supersede; không rewrite completion
  history. Gate C/PLAN 55 reports đã ghi đúng optional external QA.
- Security checklist trước audit khuyến nghị derive template từ installed Audition; wording này bị loại vì conflict.
- Không còn future PLAN yêu cầu game install path, detection, backup/restore, launch/login, process observation,
  gameplay automation, anti-cheat, hooking/injection hoặc memory editing.

## Exact completed PLAN 56–60 và milestone

- PLAN 56 — Batch Build Summary — **Milestone 3: Editor Completion**.
- PLAN 57 — AI Provider Abstraction — **Milestone 4: AI Workflow & Commercial Backend**.
- PLAN 58 — Supabase Auth — **Milestone 4: AI Workflow & Commercial Backend**.
- PLAN 59 — Backend Trusted Gateway — **Milestone 4: AI Workflow & Commercial Backend**.
- PLAN 60 — Credit Ledger — **Milestone 4: AI Workflow & Commercial Backend**.

PLAN 61 — AI Pricing là next plan nhưng chưa được phê duyệt/triển khai bởi audit này.

## Exact revised names từ PLAN 56 đến cuối roadmap

- PLAN 56 — Batch Build Summary
- PLAN 57 — AI Provider Abstraction
- PLAN 58 — Supabase Auth
- PLAN 59 — Backend Trusted Gateway
- PLAN 60 — Credit Ledger
- PLAN 61 — AI Pricing
- PLAN 62 — AI Job System
- PLAN 63 — AI Studio UI
- PLAN 64 — AI Mask Editor
- PLAN 65 — Inpaint / Outpaint / Remove / Replace / Upscale
- PLAN 66 — Prompt Presets
- PLAN 67 — Threat Model
- PLAN 68 — Secret Separation
- PLAN 69 — Secure Local Session Storage Audit
- PLAN 70 — Server-Authoritative License/Entitlement
- PLAN 71 — Premium Template Distribution Model
- PLAN 72 — Encrypted Local Template Cache
- PLAN 73 — Template Exposure & Build-Location Decision
- PLAN 74 — Protected Workspace Hardening
- PLAN 75 — Privilege Model Review
- PLAN 76 — App Code Signing
- PLAN 77 — App Update Signing & Verification
- PLAN 78 — Binary Obfuscation Strategy
- PLAN 79 — Native AOT / Native Core Evaluation
- PLAN 80 — Client Integrity / Anti-Tamper Checks
- PLAN 81 — DLL Hijacking / Process Launch Hardening
- PLAN 82 — API Replay / Abuse Protection
- PLAN 83 — Supabase RLS Hardening
- PLAN 84 — Payment Security
- PLAN 85 — Credit Concurrency Integration Gate
- PLAN 86 — Device Sessions / Abuse Controls
- PLAN 87 — Privacy & Logging Security
- PLAN 88 — Dependency / Supply-Chain Security
- PLAN 89 — Security Test Matrix
- PLAN 90 — Release Penetration / Crack-Resistance Review
- PLAN 91 — Remote Product Catalog (Game/Mod/Template Taxonomy)
- PLAN 92 — File-Only Template Admin Tool
- PLAN 93 — Account/Profile/Credit History UI
- PLAN 94 — Payment Abstraction
- PLAN 95 — Audition AI Mod Studio Application Installer
- PLAN 96 — Audition AI Mod Studio Application Updater
- PLAN 97 — Archive File-Pipeline Integration Test Suite
- PLAN 98 — DDS File-Pipeline Compatibility Matrix
- PLAN 99 — Crash/Recovery Tests
- PLAN 100 — Performance Tests
- PLAN 101 — V1 File-Only Product Release Gate
