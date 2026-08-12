# Threat Model — Audition AI Mod Studio

## Phạm vi và giả định

Tài liệu này áp dụng cho kiến trúc đã triển khai đến PLAN 79. Sản phẩm là file-based content editor, archive builder và export tool có backend AI/commercial tùy chọn. Pipeline kết thúc ở standalone `.ab`/`.acv` được người dùng xuất ra.

Không coi Audition installation, game folder, game process, launcher, login, anti-cheat, gameplay, mod installation, backup/restore game archive hoặc in-game QA là asset, trust boundary hay workflow của ứng dụng.

Nhãn trạng thái control:

- **Implemented**: có code/test trong repository.
- **Planned**: roadmap yêu cầu nhưng chưa triển khai.
- **Operational**: deployment owner phải cấu hình/chứng minh ngoài repository.

Không suy diễn `Operational` hoặc `Planned` thành production-ready. Concrete AI provider và live PostgreSQL integration hiện chưa được verified.

## Mục tiêu bảo mật

1. Secret và economic authority chỉ tồn tại ở trusted server/deployment boundary.
2. User chỉ đọc/sửa tài nguyên thuộc ownership của verified identity.
3. Credit/payment/entitlement không thể được cấp bằng client state, replay hoặc request tampering.
4. File/process input không thoát managed workspace, không command injection và không sửa pristine template.
5. Update/tool/package chỉ được dùng sau integrity/authenticity validation phù hợp.
6. Lỗi, cancellation và outcome không chắc chắn fail closed, không publish partial success hoặc charge sai.
7. Local file editing/build/export vẫn hoạt động khi cloud/auth/provider unavailable.

## Assets

| Asset | Yêu cầu | Authority/owner | Exposure chấp nhận được |
|---|---|---|---|
| AI provider secret | Confidentiality, rotation | Backend operations | Không vào desktop/repo/log |
| Supabase privileged keys | Confidentiality, least privilege | Backend/database operations | Publishable client key không phải privileged key |
| Credit balances/transactions | Integrity, consistency, auditability | Gateway + private PostgreSQL | Client chỉ đọc projection của chính user |
| Payment state | Integrity, authenticity, idempotency | Payment backend | Chỉ verified webhook/server reconciliation được mutate |
| Premium template catalog | Authorization, integrity, versioning | Catalog/entitlement backend | Metadata scoped; raw package chỉ sau grant |
| Raw pristine archive templates | Integrity, controlled disclosure | Template distribution + workspace services | Working copy riêng; client được cấp có thể cuối cùng quan sát nội dung |
| Mod manifests/prompt presets | Integrity, schema/version correctness | Code-owned catalog hoặc owned user content | Không mang commercial authority |
| App binary/IP | Integrity; confidentiality chỉ best-effort | Release engineering | .NET decompile/patch là residual risk, không phải security boundary |
| Update channel | Authenticity, freshness, rollback resistance | Release/update service | Manifest/package signed và hash verified |
| User tokens/session material | Confidentiality, expiry, rotation | Supabase Auth + Windows user context | Access token chỉ tới exact HTTPS origin; refresh material qua secure store |
| Private AI input/output | Confidentiality, ownership, bounded retention | Authenticated content store | Không public raw URL/path; owner/kind enforced |
| ACV tool/keydat | Tool integrity và deterministic execution | Release/tool policy | Keydat là companion artifact, không phải secret/DRM |

## Attackers và năng lực

| Attacker | Năng lực giả định | Không giả định |
|---|---|---|
| Remote unauthenticated | Gửi malformed/oversized/replayed HTTP, dò endpoint | Không có valid user token |
| Authenticated malicious user | Sửa client/request, dùng tài nguyên của chính họ, đoán ID user khác | Không có server/database privileged credential |
| Local standard user/process | Đọc file cùng Windows account khi ACL cho phép, sửa binary/config/temp, attach debugger | Không tự có Administrator/kernel access |
| Local Administrator/fully compromised account | Dump memory, đọc Credential Manager trong user context, inject/replace process | Secure local storage không bảo vệ tuyệt đối trước attacker này |
| Network attacker | MITM/DNS/redirect attempt, capture/replay traffic | Không phá TLS hợp lệ nếu endpoint/certificate đúng |
| Supply-chain attacker | Tamper dependency/tool/update/installer/build artifact | Không có signing key được bảo vệ đúng |
| Compromised backend/operator | Đọc server secret/content, mutate authority trong phạm vi role | Application control không loại bỏ insider risk |

## Trust boundaries

```text
┌──────────────────────── Untrusted/local boundary ────────────────────────┐
│ User files, imported images/DDS, prompt/preset, desktop UI/process       │
│   │ strict schema/path/media/resource validation                         │
│   ▼                                                                       │
│ Managed LocalApplicationData + randomized project working copy            │
│   │ absolute hash-pinned process invocation                               │
│   ├──────────────► acv.exe / DirectXTex companion tools                   │
│   │                                                                       │
│   │ HTTPS + bearer user session; no privileged/provider secret            │
└───┼───────────────────────────────────────────────────────────────────────┘
    ▼
┌──────────────────────── Trusted server boundary ─────────────────────────┐
│ TLS termination → Supabase token validation → verified UUID principal    │
│ Gateway validation/rate policy → pricing/jobs/entitlement/content         │
│   ├──► Private PostgreSQL/RLS/functions                                   │
│   ├──► Private content store                                              │
│   ├──► AI provider (server credential only)                               │
│   └──► Payment provider/webhook verifier (planned)                        │
└───────────────────────────────────────────────────────────────────────────┘

┌──────────────────────── Release trust boundary ──────────────────────────┐
│ Protected signing key → signed installer/update manifest/package         │
│                         → client signature/hash/freshness verification    │
└───────────────────────────────────────────────────────────────────────────┘
```

## Data flows

### Local file workflow

```text
Explicit template/file selection
  → canonical path + reparse-point checks
  → isolated workspace + verified pristine-to-working copy
  → extract/scan/import/edit/Apply/validate
  → atomic build/pack/verify/export standalone .ab/.acv
  → END
```

Security invariants: không discover game installation; không sửa pristine/global template; tool path absolute; structured arguments/stdin; intermediate/failed output không được promote.

### Authenticated AI workflow

```text
Prompt/preset + source/mask
  → desktop bounds + secure session lookup
  → authenticated upload/quote/enqueue
  → verified Supabase UUID
  → server pricing + transactional reserve + durable lease
  → provider-neutral execution + media validation
  → private owner-scoped output + capture/release/reconciliation
  → bounded authenticated download → InternalImage preview
  → explicit user Approve → existing atomic Apply pipeline
```

Preset selection, quote và preview không tự Apply. Client không gửi final cost, balance, provider credential, trusted model ID hoặc arbitrary user UUID.

### Update workflow

```text
Release signing service
  → signed versioned manifest + package hash/signature
  → HTTPS distribution
  → client verifies origin, signature, hash, version/freshness
  → atomic stage/swap/rollback
```

PLAN 67 chỉ mô hình hóa flow; signing/update production controls vẫn phải được chứng minh tại PLAN/deployment tương ứng.

## Phương pháp xếp hạng

Likelihood và Impact dùng thang 1–5. Score = Likelihood × Impact.

- Critical: 20–25
- High: 12–19
- Medium: 6–11
- Low: 1–5

Priority dưới đây là inherent risk trước control. Residual được đánh giá theo control hiện có và gap deployment.

## Ranked abuse cases

| # | Abuse case | L | I | Score | Priority | Control chính/trạng thái | Residual |
|---:|---|---:|---:|---:|---|---|---|
| 1 | Steal provider/service-role/payment/signing secret từ client/repo/log | 4 | 5 | 20 | Critical | Secret separation, redaction, server config **Implemented**; CI scan/managed secret/signing **Planned/Operational** | High cho compromised backend/operator |
| 2 | Patch local license/credit/`IsPremium` check để lấy premium/AI | 5 | 4 | 20 | Critical | Server pricing/credit/identity và signed scoped grant **Implemented** | Medium; durable record/nonce deployment còn thiếu |
| 3 | Concurrent/replayed spend gây double charge/spend | 4 | 5 | 20 | Critical | SQL transaction, advisory/row locks, idempotency, lease **Implemented offline-tested** | Medium; live PostgreSQL chưa verified |
| 4 | Fake payment callback cấp credits | 4 | 5 | 20 | Critical | Raw-body HMAC/timestamp, server catalog, append-only dual idempotency **Implemented contract** | Medium; live Stripe/secret/ops và PLAN 85 DB gate chưa verified |
| 5 | Cross-user content/job/preset/template access | 4 | 5 | 20 | Critical | Verified UUID, owner filters/content kind **Implemented**; cloud preset/template storage **Planned** | Medium/High tùy deployment/RLS test |
| 6 | Tamper update/installer/dependency để chạy code | 4 | 5 | 20 | Critical | App signing pipeline + signed manifest/hash/version/publisher verification **Implemented contract**; live signer/installer/SBOM **Operational/Planned** | Medium/High |
| 7 | Path traversal/reparse/malicious archive or image escapes workspace | 4 | 5 | 20 | Critical | Canonical relative paths, reparse checks, bounds, atomic promotion **Implemented** | Medium; parser/tool vulnerabilities còn lại |
| 8 | Replace `acv.exe`/DLL hijack | 4 | 4 | 16 | High | Absolute path, SHA-256 manifest/copy/prelaunch verify, isolated cwd **Implemented** | Medium; TOCTOU/admin attacker |
| 9 | MITM/redirect/replay API request | 3 | 5 | 15 | High | Exact HTTPS origin, redirects off, token validation, idempotency **Implemented**; TLS termination/rate limit **Operational/Planned** | Medium |
| 10 | Malformed provider/media result gây publish/charge sai | 3 | 5 | 15 | High | Bounded media validator, persist-before-complete, reconciliation **Implemented contract** | Medium/High; concrete provider/live storage chưa verified |
| 11 | Copy raw premium template/archive | 5 | 3 | 15 | High | Authenticated signed/scoped distribution + DPAPI-wrapped AES-GCM local cache **Implemented** | High; authorized user can recover workspace/final archive |
| 12 | Dump process memory lấy user token/content | 3 | 4 | 12 | High | Short-lived token/rotation, minimal lifetime, secure store **Implemented partly** | High với Administrator/compromised account |
| 13 | Inspect temp/workspace lấy raw assets/masks | 4 | 3 | 12 | High | Randomized managed workspace, protected owner/SYSTEM/admin DACL, reparse gates, cleanup/recovery **Implemented** | Medium; same-account/admin access |
| 14 | Decompile .NET code/copy IP | 5 | 2 | 10 | Medium | No embedded authority; signing/obfuscation/AOT chỉ hardening | High cho IP confidentiality, Low cho server authority |
| 15 | Prompt/preset injection điều khiển provider/price/path | 3 | 3 | 9 | Medium | Bounded typed fields, allowlisted options, no executable templates **Implemented** | Low/Medium; model-output safety provider-specific |
| 16 | Log/crash artifact leak token/prompt/provider body | 3 | 3 | 9 | Medium | Redacted value objects, stable diagnostics, no raw body logging **Implemented**; release artifact scan **Planned/Operational** | Medium |
| 17 | Resource exhaustion bằng oversized file/body/job | 3 | 3 | 9 | Medium | Body/media/pixel/path/history caps, cancellation **Implemented**; distributed rate limiting **Planned** | Medium |

## Owner / mitigation / test mapping

| Threat/control | Owner | Mitigation | Evidence/test hiện tại | Gap/next action |
|---|---|---|---|---|
| Client secret theft | Security + Backend Ops | Server-only config, redaction, no privileged contract | `GatewayArchitectureTests`, auth/provider config tests, repository scan | PLAN 68 CI scan + rotation runbook |
| Token theft/rotation | Desktop Security + Auth | Credential Manager, redirects off, bounded parse, serialized refresh | `WindowsCredentialSessionStoreTests`, `SupabaseAuthServiceTests` | PLAN 69 release/plaintext/concurrency audit |
| Forged identity/cross-user | Gateway + Data | Supabase `/auth/v1/user`, UUID from principal, owner filters/RLS | `SupabaseAccessTokenValidatorTests`, `TrustedGatewayEndpointTests`, AI job/content tests | Live RLS/storage integration |
| Credit replay/race | Backend + Data | Transaction, advisory/row locks, request hash/idempotency | `CreditLedgerContractTests`, `AiJobContractTests` | Live PostgreSQL concurrency gate |
| Fake payment | Payments + Backend | Raw-body HMAC/timestamp, server-priced catalog, idempotent append-only event/payment ledger | `PaymentSecurityTests`, migration contract tests | Live Stripe/secret rotation/alert và PLAN 85 DB gate |
| Provider ambiguity | AI Backend | Durable lease, reconciliation state, no blind retry | `AiExecutionTests`, migration contract tests | Operations reconciliation tooling |
| Malicious upload/output | Gateway + Imaging | Size/MIME/signature/dimension/hash validation | `AiExecutionTests`, imaging/import/DDS tests | Fuzzing và concrete provider verification |
| Path traversal/reparse | Desktop Infrastructure | Central path abstraction, no follow reparse, atomic writes | `PathSecurityTests`, workspace/project/archive tests | Re-run on supported filesystems/release image |
| Tool replacement/DLL hijack | Archive/DDS + Release | Absolute path, pinned SHA-256, isolated cwd, prelaunch check | archive integrity/process runner tests | Signed tool package; document TOCTOU residual |
| Temp disclosure | Desktop Infrastructure | Random workspace, protected DACL, lifecycle cleanup, crash recovery | workspace ACL/reparse/cleanup/recovery/concurrency tests | Same-account/admin/process-memory residual |
| Update tampering | Release Engineering | Signed/timestamped package; ES256 manifest, exact URL/hash/version/publisher | `AppCodeSigningPolicyTests`, `AppUpdateVerificationTests` | Production signing key/HSM, release endpoint and E2E installer gate |
| Dependency compromise | Release Engineering | Lock/pin, vulnerability audit, provenance/SBOM | `dotnet list package --vulnerable` gate | SBOM/license/provenance pipeline |
| Prompt/preset authority escalation | AI Desktop + Gateway | Typed allowlist, schema/version bounds, selection-only behavior | `PromptPresetTests`, `LocalPromptPresetStoreTests`, `AiStudioViewModelTests` | Authenticated cloud preset adapter/store |
| Premium bypass/distribution | Entitlement Backend + Storage | Signed scoped expiring ES256 grant, replay defense, signed manifest, private short-lived access, encrypted derived cache | `EntitlementGrantTests`, `PremiumTemplateDistributionTests`, `EncryptedPremiumTemplateCacheTests` | Durable catalog/record/nonce/private-storage deployment và download wiring |
| Secret/log/crash leak | Security + SRE | Structured diagnostics, redacted types, artifact scans | architecture tests and manual scan | PLAN 68 automated source/build/log/crash scan |
| DoS/rate abuse | Gateway Ops | Bounds/timeouts/cancellation; per-user distributed limits | request size/schema tests | Rate-limit deployment design |

## STRIDE theo boundary

| Boundary | S | T | R | I | D | E |
|---|---|---|---|---|---|---|
| Desktop ↔ Gateway | Forged/stolen bearer | Body/operation tamper | Missing audit correlation | Token/prompt leak | Request flood | Client asserts price/user |
| Gateway ↔ Supabase/PostgreSQL | Forged subject/role | SQL/function misuse | Incomplete durable audit | Connection/SQL error leak | Pool/lock exhaustion | service-role overprivilege |
| Gateway ↔ AI/content provider | Provider impersonation | Output/reference tamper | Ambiguous provider outcome | Secret/content leak | Timeout/oversized output | Raw provider controls exposed |
| Stripe → Gateway | Forged webhook | Body/event/payment tamper | Duplicate/conflicting event | Secret/payment data leak | Webhook flood/retry storm | Provider/client chooses credit |
| Desktop ↔ filesystem/tools | Symlink/reparse identity | Binary/archive/DDS tamper | Weak operation evidence | Temp/pristine exposure | Resource bomb/deadlock | Command/path injection |
| Updater ↔ release channel | Fake publisher | Package/manifest rollback | Missing provenance | Signing key leak | Update outage | Arbitrary code execution |

## Residual risks được chấp nhận/đang mở

1. Một authorized user nhận hoặc build complete standalone archive có thể recover nội dung. Encryption/cache/obfuscation chỉ giảm casual harvesting, không tạo DRM tuyệt đối.
2. Administrator hoặc fully compromised Windows account có thể dump process memory, đọc token/content và patch binary. Credential Manager/DPAPI không giải quyết attacker này.
3. SHA-256 xác nhận identity/integrity kỳ vọng nhưng không thay chữ ký publisher và không tự chứng minh runtime compatibility.
4. External AI provider adapter, provider network, private content store deployment và live PostgreSQL concurrency chưa verified; provider-neutral/fake tests không phải production evidence.
5. TLS termination, WAF/rate limiting, secret manager, monitoring, backup/restore và incident response là operational controls chưa được repository chứng minh.
6. Stripe payment webhook contract đã triển khai nhưng live endpoint/secret/product, secret rotation, alert/dispute operations và real fulfillment transaction chưa verified. Premium package concrete storage/deployment cũng chưa verified.
7. Parser/native/tool zero-day và supply-chain compromise vẫn có thể tồn tại dù input bounds/hash pin.
8. App binary/IP có thể bị decompile. Bảo vệ business authority bằng server boundary quan trọng hơn cố giữ client code bí mật.
9. Prompt và AI output có thể chứa sensitive user content; retention/deletion policy phải được deployment/product owner chốt trước production.
10. Runtime hiện vẫn `requireAdministrator` theo PLAN 02 cho đến migration gate sau PLAN 75; app-wide elevation làm tăng blast radius và có thể đổi profile khi UAC dùng alternate credential. ADR-0002 đã chọn future `asInvoker`, nhưng chưa triển khai.

## Security gates tối thiểu trước production

- PLAN 68 secret separation + automated scan source/build/log/crash và rotation runbook.
- PLAN 69 secure session compatibility/corruption/concurrency/release plaintext audit.
- PLAN 70 signed/scoped/expiring entitlement grants với ownership/audience/replay tests.
- Live PostgreSQL migration/RLS/concurrency test bằng approved staging/local database.
- TLS/rate-limit/monitoring deployment review; concrete provider/media/content-store integration test.
- Signed/timestamped app/installer/update; protected signing key; SBOM, licenses và provenance.
- Incident runbook cho secret/token/signing-key compromise, payment dispute và provider ambiguity.

## Quy trình duy trì

Security owner phải cập nhật tài liệu khi trust boundary, data flow, provider, payment, storage, update hoặc template distribution thay đổi. Mỗi threat mới cần owner, mitigation, automated/operational test và residual risk. Mọi tuyên bố production phải liên kết evidence của deployment cụ thể; test fake/offline không được đổi nhãn thành live verification.

## Native AOT residual risk từ PLAN 79

PLAN 79 không adopt Native AOT vì Release x64 publish analyzer fail trên JSON dynamic-code paths. Managed binary/IP vẫn có
thể bị decompile; AOT tương lai cũng chỉ tăng chi phí phân tích, không thay server authority hoặc ngăn Administrator/runtime
plaintext capture. Việc probe dừng trước native link có nghĩa WinUI/XAML, SkiaSharp và P/Invoke runtime vẫn chưa verified,
không phải bằng chứng rằng các thành phần đó tương thích hay không tương thích tuyệt đối.

## Client integrity control từ PLAN 80

Moderate integrity gate kiểm executable signer khi production policy yêu cầu, trusted manifest SHA-256 và exact
resource/companion length+hash. Tamper/missing/traversal/reparse đưa risky capabilities về diagnostics-only, không phản ứng
anti-debug hoặc phá hủy dữ liệu. Control tăng khả năng phát hiện casual/local modification nhưng không chống được
Administrator có thể patch process/memory hoặc thay đồng thời unsigned authority. Vì vậy expected manifest hash phải được
bind vào signed release/package/update chain; production binding hiện chưa verified. Archive pre-launch hash gate vẫn là
control gần execution nhất cho `acv.exe`, với residual TOCTOU đã được chấp nhận.

## DLL planting / process launch từ PLAN 81

Attacker có thể đặt fake executable hoặc DLL vào `PATH`, cwd, workspace, Temp/Downloads/project/export hoặc dùng reparse/race.
Production launcher không search executable; app/child DLL search bỏ cwd/user dirs, child nhận system-only `PATH`, và exact
tool bytes/directory được kiểm sát launch. ACV/DDS typed arguments không tạo shell command. Residual same-user/Administrator
TOCTOU còn tồn tại giữa verify và OS image open; current elevation làm impact lớn hơn, nên ADR-0002 vẫn là mitigation ưu tiên.
Không có control nào theo dõi/can thiệp game process.

## API replay / abuse residual risk từ PLAN 82

Gateway hiện reject cleartext, validate short-lived Supabase JWT online, giới hạn request theo IP trước auth và verified
user sau auth, enforce type/size/ownership, giữ durable idempotency cho charged job và ghi audit đã giảm dữ liệu. Replay
cùng idempotency key/payload là retry deterministic, không phải charge mới; entitlement grant vẫn one-time nonce.

Residual risk: limiter chỉ local process nên botnet hoặc nhiều replica cần edge/distributed control; proxy allowlist/TLS
certificate, Supabase live JWT expiry, production stress threshold, centralized audit sink/alert/retention chưa có evidence.
Compromised bearer token vẫn dùng được đến `exp`; Gateway không tuyên bố instant revocation hay chống account takeover.

## Supabase RLS residual risk từ PLAN 83

Own-row SELECT policy + column grants chặn user A đọc row/cột internal của user B và chặn mọi client DML/RPC. Idempotency
table, provider/lease capability và authority reference vẫn server-only. Real PostgreSQL negative gate đã xác minh behavior.

Residual risk nằm ở `service_role`/superuser/BYPASSRLS, Supabase exposed-schema và migration-owner configuration; leak
service-role secret vượt qua RLS. Staging/live Supabase chưa verified. Foreign-key/unique enforcement có PostgreSQL covert
channel semantics nhưng client không có DML. PLAN 60 function ambiguity đã được phát hiện và chuyển thành bắt buộc PLAN 85.

## Payment webhook residual risk từ PLAN 84

Giả callback, body tamper, stale replay, wrong environment, provider-selected credit và duplicate event/payment bị chặn bởi
raw-body HMAC, signed timestamp, live-mode/catalog binding, advisory lock, unique identities và stored request hashes. Client
không có success/grant authority. Payment event ledger là append-only/server-only và chỉ exact function gọi `credit_grant`.

Residual risk gồm compromised Stripe/Gateway secret, endpoint flooding nhiều replica, provider account takeover, event đến
trễ hơn tolerance nhưng được Stripe ký mới, secret rotation sai, dispute/refund/chargeback và thiếu alert/reconciliation.
Repository chưa chứng minh TLS edge/live Stripe. PLAN 60 ambiguity làm real grant chưa production-ready cho tới PLAN 85;
vì vậy PLAN 84 chỉ là implemented contract, không phải live payment certification.
