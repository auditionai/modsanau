# Threat Model — Audition AI Mod Studio

## Phạm vi và giả định

Tài liệu này áp dụng cho kiến trúc đã triển khai đến PLAN 79. Sản phẩm là file-based content editor, archive builder và export tool có backend AI/commercial tùy chọn. Pipeline kết thúc ở standalone `.ab`/`.acv` được người dùng xuất ra.

Không coi Audition installation, game folder, game process, launcher, login, anti-cheat, gameplay, mod installation, backup/restore game archive hoặc in-game QA là asset, trust boundary hay workflow của ứng dụng.

Nhãn trạng thái control:

- **Implemented**: có code/test trong repository.
- **Planned**: roadmap yêu cầu nhưng chưa triển khai.
- **Operational**: deployment owner phải cấu hình/chứng minh ngoài repository.

Không suy diễn `Operational` hoặc `Planned` thành production-ready. Concrete AI provider và Supabase staging/live hiện chưa được verified; local PostgreSQL concurrency đã verified ở PLAN 85.

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
| 3 | Concurrent/replayed spend gây double charge/spend | 4 | 5 | 20 | Critical | SQL transaction, advisory/row locks, idempotency **Local PostgreSQL verified** | Low/Medium; staging/live/load/failover chưa verified |
| 4 | Fake payment callback cấp credits | 4 | 5 | 20 | Critical | Raw-body HMAC/timestamp, catalog, dual idempotency; local payment-to-grant **Verified** | Medium; live Stripe/secret/ops chưa verified |
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
| Credit replay/race | Backend + Data | Transaction, advisory/row locks, request hash/idempotency | `CreditConcurrencyIntegrationTests` on real PostgreSQL | Supabase staging/live, load/failover |
| Fake payment | Payments + Backend | Raw-body HMAC/timestamp, server-priced catalog, idempotent append-only event/payment ledger | Payment tests + real PostgreSQL payment-to-grant replay | Live Stripe/secret rotation/alert |
| Provider ambiguity | AI Backend | Durable lease, reconciliation state, no blind retry | `AiExecutionTests`, migration contract tests | Operations reconciliation tooling |
| Malicious upload/output | Gateway + Imaging | Size/MIME/signature/dimension/hash validation | `AiExecutionTests`, imaging/import/DDS tests | Fuzzing và concrete provider verification |
| Path traversal/reparse | Desktop Infrastructure | Central path abstraction, no follow reparse, atomic writes | `PathSecurityTests`, workspace/project/archive tests | Re-run on supported filesystems/release image |
| Tool replacement/DLL hijack | Archive/DDS + Release | Absolute path, pinned SHA-256, isolated cwd, prelaunch check | archive integrity/process runner tests | Signed tool package; document TOCTOU residual |
| Temp disclosure | Desktop Infrastructure | Random workspace, protected DACL, lifecycle cleanup, crash recovery | workspace ACL/reparse/cleanup/recovery/concurrency tests | Same-account/admin/process-memory residual |
| Update tampering | Release Engineering | Signed/timestamped MSIX; ES256 product/channel/rollout manifest, exact HTTPS origin/length/hash/version/publisher, MSIX identity, single-flight typed deployment | `AppCodeSigningPolicyTests`, `AppUpdateVerificationTests`, `MsixPackageIdentityVerifierTests` | Production signing key/HSM, release endpoint/CDN and actual trusted deployment gate |
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
4. External AI provider adapter/network, private content store và Supabase staging/live chưa verified; local PostgreSQL concurrency PASS không phải production topology evidence.
5. TLS termination, WAF/rate limiting, secret manager, monitoring, backup/restore và incident response là operational controls chưa được repository chứng minh.
6. Stripe payment webhook contract và local PostgreSQL fulfillment transaction đã verified, nhưng live endpoint/secret/product, secret rotation, alert/dispute operations chưa verified. Premium package concrete storage/deployment cũng chưa verified.
7. Parser/native/tool zero-day và supply-chain compromise vẫn có thể tồn tại dù input bounds/hash pin.
8. App binary/IP có thể bị decompile. Bảo vệ business authority bằng server boundary quan trọng hơn cố giữ client code bí mật.
9. Prompt và AI output có thể chứa sensitive user content; retention/deletion policy phải được deployment/product owner chốt trước production.
10. PLAN 90 đã chuyển runtime sang `asInvoker` và verify file-only/real-tool gates dưới medium-integrity token. Dữ liệu lịch
    sử trong alternate-admin profile không được auto-discover; installer/update elevation vẫn là boundary chưa production-verified.

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
channel semantics nhưng client không có DML. PLAN 60 function ambiguity đã được PLAN 85 sửa và verified trên PostgreSQL thật.

## Payment webhook residual risk từ PLAN 84

Giả callback, body tamper, stale replay, wrong environment, provider-selected credit và duplicate event/payment bị chặn bởi
raw-body HMAC, signed timestamp, live-mode/catalog binding, advisory lock, unique identities và stored request hashes. Client
không có success/grant authority. Payment event ledger là append-only/server-only và chỉ exact function gọi `credit_grant`.

Residual risk gồm compromised Stripe/Gateway secret, endpoint flooding nhiều replica, provider account takeover, event đến
trễ hơn tolerance nhưng được Stripe ký mới, secret rotation sai, dispute/refund/chargeback và thiếu alert/reconciliation.
Repository chưa chứng minh TLS edge/live Stripe. PLAN 85 đã chứng minh local database grant/replay nhưng PLAN 84 vẫn chỉ là
implemented contract, không phải live payment certification.

## Credit concurrency residual risk từ PLAN 85

Real PostgreSQL gate chứng minh khác-key wallet race không overspend, same-key duplicate/conflict không double-mutate,
capture-vs-release chỉ có một terminal state và concurrent refund không vượt capture. Persisted ledger/state được đối chiếu
sau rejection; payment grant reuse cùng repaired ledger và idempotency.

Residual risk còn lại là Supabase staging/live role/topology, connection pool exhaustion, lock timeout/deadlock dưới workload
khác, multi-region/failover, operational retry, backup/restore và monitoring. Service-role/database compromise vẫn vượt qua
business function boundary. Không có evidence nào cho phép client set balance/cost/refund/payment amount.

## Device/session abuse residual risk từ PLAN 86

Stolen bearer kết hợp session UUID có thể gọi cloud đến khi bearer hết hạn hoặc binding bị revoke; UUID không phải possession
proof. Patched client có thể tạo DeviceId mới, nên control này chỉ giới hạn số thiết bị theo verified user và tạo revoke point,
không phải hardware DRM. Advisory lock ngăn concurrent enrollment vượt limit trong một PostgreSQL authority; multi-region,
failover và distributed abuse monitoring chưa production-verified.

Gateway không tin client `UserId`, IP hay device metadata. Cross-user revoke/read bị chặn; missing/revoked session fail closed
trước AI/premium service. Revoke không phá local availability. Account recovery, stolen-bearer response, unusual-login alert,
support override và Supabase live RLS/role topology vẫn là operational/product work chưa được chứng minh.

## Logging và diagnostic export residual risk từ PLAN 87

Central sink redaction và lớp quét lại khi export làm giảm khả năng password, token/JWT, signed URL, provider secret và payment
secret xuất hiện trong log/bundle. Allowlist export loại project, ảnh, template, archive, workspace, settings và credential;
không có auto-upload hoặc user-content opt-in.

Residual risk vẫn gồm secret tùy ý không khớp tên/pattern, dữ liệu đã bị caller render thành chuỗi không có ngữ cảnh, quyền đọc
của same-user/Administrator và việc người dùng gửi bundle qua kênh không an toàn. Redactor không thay thế nguyên tắc không log
raw content/secret, OS ACL, support consent/retention/deletion, monitoring hoặc incident response. Các control vận hành này chưa
production-verified.

## Dependency / supply-chain residual risk từ PLAN 88

Exact NuGet locks, source/signature policy, full-SHA Actions, vulnerability audit và tracked SPDX SBOM làm graph/review drift có
thể phát hiện. Native inventory giữ riêng version/hash/signer/license/provenance; commercial release gate không cho ACV/template/
game asset đi vào package khi chưa có written rights/provenance approval.

Residual risk còn gồm advisory lag/zero-day, compromised NuGet/GitHub/publisher/signing account, malicious signed package,
transitive native payload, CI runner compromise và mismatch giữa repository SBOM với exact published file inventory. DirectXTex
local verification không chứng minh mọi mirror/CDN artifact. `acv.exe` known hash không chứng minh origin/quyền phân phối;
proprietary commercial distribution hiện bị block. Production HSM, build attestation, immutable artifact repository và legal
approval vẫn chưa được repository xác minh.

## Negative security matrix từ PLAN 89

Threat regression gate bao phủ tool/template/package/update tamper, traversal, forged credit authority, expired bearer,
duplicate/concurrent spend, cross-user job access, plaintext-token leakage và malformed DDS/archive. Các oracle kiểm tra cả
persisted cardinality/state thay vì chỉ HTTP result; malformed archive còn kiểm tra input/tool không đổi và không workspace escape.

Residual risk: local PostgreSQL không đại diện đầy đủ topology/role/proxy của Supabase production; known-hash ACV không chứng minh
provenance; regex secret scan có thể bỏ sót dữ liệu tùy ý; malformed fuzz corpus còn hữu hạn. PLAN 90 phải review thủ công các
đường decompile/license patch/API manipulation/template harvesting/temp-cache/RLS/update/DLL loading mà không coi obfuscation là
authorization boundary.

## Release adversarial review từ PLAN 90

Review tám path xác nhận business authority không nằm trong managed client. Hai mitigation mới giảm attack surface/exposure:
runtime `asInvoker` thay app-wide elevation, và public Release layout không mang PDB/source/private map/key/proprietary fixture.
RLS adversarial test còn chứng minh string claim `service_role` không đổi actual authenticated role, BYPASSRLS hay visibility.

Residual risk còn lại: decompile managed IL/IP, authorized template/plaintext output recovery, same-user/Admin memory/TOCTOU,
production authenticator/role topology, signer/HSM/CDN/installer, live TLS/WAF/rate limit/Supabase/Stripe và external independent
review. Đây là release blockers/accepted product limits tương ứng, không được “fix” bằng client anti-tamper hoặc hardware DRM.

## Remote catalog residual risk từ PLAN 91

Authenticated Gateway cộng chữ ký document, strict schema/relationship validation, monotonic revision và atomic verified cache
giảm nguy cơ catalog giả, tamper, rollback và cache corruption. Catalog không cấp entitlement và không chứa executable/game-path
authority, nên client patch hoặc field premium giả không tạo commercial/runtime capability.

Residual risk gồm catalog signing key hoặc Gateway configuration bị compromise, key rotation sai, authorized metadata độc hại
nhưng vẫn nằm trong bounded schema, đồng hồ/rollback policy nhiều thiết bị và thiếu live publication/monitoring evidence. Local
contract tests không chứng minh production signer, TLS edge, CDN, availability hoặc incident response.

## Template admin residual risk từ PLAN 92

Role-first authorization, isolated working copy, strict label/manifest consistency, source-handle hash verification, authenticated
encryption, ES256 signature và immutable atomic version commit giảm nguy cơ unauthorized scan, traversal, source swap, metadata
tamper và concurrent overwrite. Audit chỉ giữ subject/identity/count/time/action, không giữ local source path hoặc raw content.

Residual risk còn gồm admin account/host hoặc injected key bị compromise, same-user/Administrator đọc memory/temp workspace,
malicious nhưng hợp lệ DDS/parser input, mất khóa mã hóa, rollback/retention sai ở storage và operator publish nhầm nội dung có
bản quyền. CBC-HMAC format là internal at-rest envelope, không thay entitlement, legal authorization hoặc transport security.
Repository chưa chứng minh production IAM, HSM/KMS, malware scanning sandbox, object-store atomic semantics, centralized audit
sink/alert hay disaster recovery.

## Account/profile/history residual risk từ PLAN 93

Verified-principal ownership, read-only repeatable snapshot, bounded history, response consistency checks và no-cache UI giảm
nguy cơ forged user, cross-user disclosure, client-forged balance, stale optimistic state và resource exhaustion. Endpoint không
nhận mutation/price/payment-success field và không mở quyền ledger mới.

Residual risk gồm stolen bearer đọc được account data đến khi token hết hạn/revoke, compromised Gateway/service-role vượt owner
filter, traffic/memory capture bởi same-user/Administrator, inference từ transaction timing và sai lệch semantics nếu production
schema drift. Local/test evidence không chứng minh live Supabase RLS/IAM, TLS edge, distributed rate limit, monitoring, retention,
account deletion/export workflow hay incident response.

## Payment abstraction residual risk từ PLAN 94

Provider-neutral typed result, fail-closed resolver và application orchestration giảm nguy cơ provider-specific object trở thành
domain authority, test adapter lọt vào production, pending/outage bị đoán thành success hoặc provider mismatch đi tới ledger.
Stripe adapter vẫn giữ exact raw-body HMAC/timestamp/live-mode/catalog binding; PLAN 84/85 PostgreSQL identity, lock và append-only
grant tiếp tục xử lý duplicate/concurrent/out-of-order success.

Refund/failure/expiry event hiện chỉ được acknowledge không mutation vì reversal policy chưa được roadmap định nghĩa. Residual
risk gồm compromised Stripe secret/account, signed nhưng malicious provider data, delayed events ngoài tolerance với signature mới,
provider API/version drift, missing durable pending/reconciliation queue, refund/dispute policy chưa có và outage nhiều replica.
Local contract/PostgreSQL evidence không chứng minh live Stripe, Supabase production, TLS/WAF, secret rotation, monitoring hoặc
incident response.

## Installer residual risk từ PLAN 95

MSIX package identity, read-only package volume, signed app payload/package, exact version/hash/publisher scan và declarative
per-user deployment giảm nguy cơ binary planting, partial install, unsafe privileged custom action, unsigned production publish
và uninstall xóa nhầm user content. Scanner fail closed với helper/proprietary fixture/private material; installer không có
game path, mod install hoặc arbitrary update URL authority.

Residual risk gồm compromised production certificate/HSM/runner/publisher account, malicious dependency nhưng vẫn được ký,
Windows package deployment vulnerability, prerequisite/license drift, per-user same-user write surface ngoài package và migration
từ một release production lịch sử chưa tồn tại. Local unsigned/test deployment không chứng minh production trust, timestamp,
enterprise policy, Store/CDN hoặc revocation/incident response. PLAN 96 phải reuse PLAN 77 verification và không tạo trust domain
hay downloader thứ hai.

## Updater residual risk từ PLAN 96

Manifest replay không thể ép downgrade vì four-part installed-version comparison; rollout/channel/product và package identity
đều nằm sau signed authority và trước Windows handoff. Random `.partial`, exact byte/hash, MSIX manifest check, WinVerifyTrust,
single-flight và build/export deferral giảm supply-chain substitution, partial promotion, race và mất dữ liệu do restart cưỡng bức.
Windows PackageManager với `DeploymentOptions.None` giữ atomic platform rollback/failure semantics; không có manual file swap,
shell, elevation, game path hay game process authority.

Residual risk gồm compromised update-signing key, production app certificate/HSM/runner/CDN account, same-user local process
TOCTOU, Windows deployment vulnerability, rollout cohort tampering trên compromised client và outage. Default production updater
fail closed vì live public key/feed/CDN/signer/timestamp chưa được cấu hình; local unsigned test evidence không chứng minh
production update hay rollback thực tế.

## Archive file-pipeline residual risk từ PLAN 97

Exact fixture hashes, isolated working copies, strict changed-set oracle và re-extracted inventory giảm nguy cơ sửa pristine,
path confusion, silent file loss, unintended non-target mutation và false-positive build success. Deterministic cancellation test
chứng minh partial export không thay destination tốt, transaction residue được dọn và operation có thể retry. Corrupt DDS copy bị
từ chối trước khi trở thành replacement hợp lệ.

Residual risk còn gồm parser/native-tool bug chưa gặp trong corpus, ACV crash/hang ngoài timeout, nondeterministic container bytes,
same-user/Administrator can thiệp workspace giữa các lần kiểm tra, disk/filesystem failure và corpus chỉ đại diện một archive/target.
Known hash không chứng minh nguồn gốc hoặc quyền phân phối proprietary artifact. Evidence logical inventory không chứng minh game
runtime compatibility; sản phẩm và test cố ý dừng ở standalone archive. PLAN 98 mới mở rộng ma trận DDS theo Mod Type và không được
suy diễn là đã hoàn thành bởi PLAN 97.

## DDS compatibility matrix residual risk từ PLAN 98

Typed stage status, exact doc drift gate, malformed corpus, checked resource bounds và re-decode giảm nguy cơ parser-only bị báo
nhầm thành support, native decoder nhận payload thiếu, metadata bị mất qua encode và archive evidence bị áp dụng sai slot. Real
working-copy corpus giúp bắt regression trên 52 sample hiện có mà không mutate pristine fixture.

Residual risk còn gồm format/header/vendor variant chưa có trong corpus, DirectXTex/native codec bug, alpha semantic khác giữa
content thực và header, GPU/runtime implementation khác editor, malicious payload vẫn nằm trong resource bound, và private corpus
chỉ đại diện một archive. BC2/BC4/BC5/BC6H/BC7 cùng resource array/cube/volume chưa có full editor model nên vẫn
`UNSUPPORTED`/`NOT VERIFIED`; Mod Type tương lai chưa có authoritative manifest nên không được suy luận support. Bằng chứng local
không chứng minh provenance, license redistribution, production catalog hoặc game runtime; pipeline vẫn dừng ở file standalone.

## Crash/recovery residual risk từ PLAN 99

Các threat chính là partial artifact bị hiểu nhầm là committed, overwrite đích trước khi candidate được verify, stale/unknown residue bị tự động trust, reparse escape trong cleanup/recovery, duplicate payment/update retry và crash sau commit nhưng trước khi caller nhận success. PLAN 99 kiểm chứng fail-closed classification, exact-byte rollback, immutable conflict/idempotent replay và clean retry bằng checkpoint xác định, cancellation, process exit, I/O failure, corruption và concurrent replay.

Residual risk còn gồm mất điện giữa lời gọi filesystem và flush thực tế, controller/storage cache của thiết bị, antivirus hoặc filesystem filter làm thay đổi semantics, crash của Windows package deployment sau handoff, corruption không nằm trong corpus và outage production PostgreSQL/provider. Không có recovery tự động nào được phép mở rộng sang game installation/runtime. Unknown state luôn cần điều tra hoặc cleanup tường minh; không được suy diễn thành committed.

## Rủi ro hiệu năng còn lại từ PLAN 100

Các threat về availability gồm input hợp lệ nhưng lớn gây allocation pressure, native codec giữ peak memory ngoài quan sát managed, batch cạnh tranh CPU/I/O, cache stampede khác key, page-cache/antivirus làm latency dao động và UI bị chậm dù background contract vẫn đúng. Bound kích thước/pixel/mip, bounded queue/concurrency, cancellation, timeout, thumbnail single-flight và immutable working-copy semantics giảm rủi ro nhưng không biến số đo local thành resource guarantee.

PLAN 100 chưa đo peak working set, native child-process peak, GUI frame/input latency, startup cold end-to-end hoặc tải đồng thời trên máy cấu hình thấp. Allocation đáng kể của thao tác ảnh 6.000×1.801 cần tiếp tục theo dõi; không tự thêm pooling/tiling khi chưa có ownership và data-lifetime analysis. Rủi ro này không cấp quyền nới resource limit, bỏ validation hoặc mở rộng sang game runtime. Chi tiết và giới hạn claim tại [PERFORMANCE_TEST_REPORT.md](PERFORMANCE_TEST_REPORT.md).

## Portable updater threats từ PLAN 102

| Threat | Mitigation PLAN 102 | Residual risk |
|---|---|---|
| Manifest tamper/MITM/CDN package replace | ES256 domain-separated envelope verify trước authority; exact HTTPS URI/size/SHA-256 | Signing key/CDN account compromise |
| Rollback/malformed version | Typed monotonic comparison; equal/no-update; lower reject | Compromised trusted signer có thể ký malicious newer release |
| ZIP Slip/link/duplicate/bomb | Canonical relative path, inventory exact, link/reparse reject, entry/expanded bounds | Parser/filesystem/filter-driver vulnerability |
| Staging tamper/TOCTOU | App verify package/files; updater reverify envelope/package/files; copy updater re-hash | Same-user attacker racing after a verify boundary |
| Argument/path injection | Exact executable path, fixed typed switches, restart allowlist, no shell/PATH, canonical roots | Same-user process có thể DoS/lock file |
| Partial install/crash/locked file | Backup before mutation, `.update-new`, signed ownership, post-verify, deterministic rollback | Power loss/storage cache failure giữa filesystem operations |
| Privilege escalation | `asInvoker`, `uiAccess=false`, no runas/service/registry/admin fallback | Portable folder không writable thì auto-update unavailable |
| User data loss | Chỉ signed app-owned paths; unknown/LocalAppData/project/export preserved; unsaved/active-work guard | App-level dirty-state model chưa bao phủ future editor surface |
| Endpoint outage/privacy | Non-blocking check, bounded retry/cadence, local-first; không project/hardware identity | Availability outage, last-check timestamp local |

Production update feed/public key build input/signer HSM/AuthentiCode/timestamp/CDN vẫn chưa verified. Local E2E dùng dedicated
TEST key và không biến test authority thành production trust root. Updater không có Audition game authority.

## Public website threats từ PLAN 103

| Threat | Mitigation PLAN 103 | Residual risk |
|---|---|---|
| Vô tình public repository desktop/private artifact | Cây public riêng, exact allowlist, export 27 file, scanner file-type/secret/symlink | Người có quyền có thể bypass CI hoặc thay allowlist ác ý |
| Secret/token trong static site | Không có cloud client/env; pattern scan; CSP `connect-src 'none'`; privileged secret bị cấm | Secret dạng mới/obfuscated có thể vượt pattern nếu review bị bỏ qua |
| Supply-chain build dependency | Build/test dùng Node standard library, lockfile không có package dependency | GitHub Actions/runner hoặc hosting account có thể bị compromise |
| XSS/third-party tracking | Static HTML/JS local, no user content/form/analytics, CSP self-only | Hosting/header drift hoặc future feature có thể mở lại attack surface |
| Phishing/misleading download | Không có download CTA; status nói rõ chưa public; internal ZIP bị loại | Domain/account takeover có thể thay nội dung ngoài repository |
| Deploy nhầm production/main | Develop-first policy, local Netlify contexts, dừng khi remote/dashboard chưa xác minh | Dashboard setting thực tế vẫn chưa truy cập/kiểm chứng |
| Supabase privilege exposure | Không tích hợp Supabase và không copy anon/service-role key trong PLAN 103 | PLAN tương lai phải review RLS/IAM/config riêng |

Hostname Netlify/Supabase phản hồi công khai không chứng minh ownership, dashboard configuration hay production trust. Vì exact GitHub remote
chưa có, không fetch/push/deploy nào được thực hiện; production giữ nguyên. PLAN 104 chưa bắt đầu.
# PLAN 104 — threat model bổ sung

| Mối đe dọa | Kiểm soát |
|---|---|
| Đoán/tuần tự hóa Device Code | Không tuần tự, alphabet không nhập nhằng, unique; code không có quyền xác thực |
| Đánh cắp session/grant | Credential Manager, TLS, bearer validation, PLAN 86 device-session binding, grant sống ngắn |
| Sửa đồng hồ/trạng thái client | Gateway dùng server time; capability chỉ tin grant ES256 và expiry đã ký |
| Redeem lặp hoặc race | Row lock, unique `(gift_code_id, device_profile_id)`, correlation id unique, transaction nguyên tử |
| Client tự cộng Credits | Không có API đó; Gift Code gọi authority `private.credit_grant` của PLAN 60 |
| Rò Gift Code | Database chỉ giữ hash; RLS/GRANT chặn client đọc bảng nhạy cảm |
| Reinstall để có identity mới | Không dùng fingerprint; giảm thiểu bằng abuse/rate limit và kiểm soát server, chấp nhận đây là giới hạn mô hình anonymous |
