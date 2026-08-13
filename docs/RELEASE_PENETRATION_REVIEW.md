# Release Penetration / Crack-Resistance Review — PLAN 90

## Kết luận

Internal manual review đã hoàn tất đủ tám attack path của roadmap. Hai finding trong repository đã được sửa:

- `F-90-01` — High: app-wide `requireAdministrator` làm tăng blast radius của parser/native tool. Runtime đã chuyển sang
  `asInvoker`; full file-only/real-tool gate chạy dưới Windows medium-integrity user không thuộc Administrators.
- `F-90-02` — Medium: public publish chứa 13 PDB, gồm absolute source paths. Release workflow hiện tắt app debug symbols,
  xóa package PDB và fail closed nếu còn PDB/source/private map/key/proprietary fixture trước signing.

Không tìm thấy client-authority bypass cần sửa. Managed IL vẫn decompile được và authorized user vẫn có thể recover plaintext
template/project/output; đây là residual risk được ghi nhận, không được che bằng tuyên bố DRM/obfuscation.

Review này là `INTERNAL MANUAL REVIEW COMPLETE`, không phải independent commissioned penetration test. Production signer/HSM,
signed installer/update CDN, live Supabase/RLS/TLS/WAF, live Stripe và external review vẫn là `RELEASE BLOCKER / NOT VERIFIED`.

Gate tái lập review trên PostgreSQL loopback, artifact Release đã chuẩn bị và standard-user token:

```powershell
powershell -NoProfile -File scripts/Invoke-ReleaseSecurityReview.ps1 `
  -ArtifactRoot artifacts/plan90-release-candidate -Configuration Release -RequireStandardUser
```

## Ma trận attack path

| ID | Attack path | Thử nghiệm/evidence | Kết quả và residual risk |
|---|---|---|---|
| RPR-01 | Decompilation exposure | Inventory managed DLL/PDB, project references, secret scan, obfuscation/AOT ADR | Không secret/server authority trong client; public PDB bị loại. IL/IP vẫn decompile; obfuscation chưa adopt và không phải authority. |
| RPR-02 | Local license patch attempts | Patched-client contract, entitlement/credit/API negative tests | Local patch chỉ đổi UI/local code; Gateway vẫn derive user, entitlement, price, credit và package access server-side. Local edit/build/export cố ý không bị license khóa. |
| RPR-03 | API request manipulation | Strict schema/unknown field, forged user/cost/operation, expired token, ownership, replay/idempotency, payment HMAC tests | Local contracts PASS. TLS edge, distributed limiter và live endpoints chưa verified. |
| RPR-04 | Template harvesting paths | Release artifact inventory, signed package/hash/length/grant tests, encrypted cache tests | Không bundle raw premium/ACV/template fixture. Authorized acquisition/working copy/output có thể bị recover; không hứa DRM tuyệt đối. |
| RPR-05 | Temp/cache extraction | Random protected workspace, reparse/ACL/cleanup/crash recovery, DPAPI-wrapped AES-GCM cache | Encryption-at-rest và ACL giảm casual access. Same-user/Admin/process-memory và plaintext active workspace còn residual. `asInvoker` giảm impact. |
| RPR-06 | RLS bypass attempts | Real PostgreSQL FORCE RLS, column/DML/RPC/cross-user/anon tests; forged role-claim test | Claim `service_role` không đổi `current_user=authenticated` hoặc cấp BYPASSRLS/cross-user row. Supabase deployed authenticator/role topology chưa verified. |
| RPR-07 | Update tampering | ES256 manifest, rollback/version/URL/hash/publisher/signature negative tests | Verification contract fail closed trước install. Production key/HSM/CDN/signed installer và E2E swap/rollback chưa verified. |
| RPR-08 | DLL search/path attacks | Absolute process path, `ArgumentList`, sanitized environment, app/System32 DLL policy, adjacent-DLL/reparse/tamper tests, real ACV/DirectXTex | Local gate PASS. Same-user race giữa final hash và OS image open là residual TOCTOU; signed production native inventory chưa verified. |

## Authority review

Desktop không tham chiếu Gateway implementation và không giữ service-role/payment/provider/master/signing secret. Client fields
như `UserId`, displayed credit, device/session UUID, local cache state hoặc success redirect không cấp authority. Gateway xác thực
Supabase bearer online, derive subject từ principal, map operation/profile/price qua server catalog và scope every job/content/
template operation theo owner. Credit/payment mutations chỉ đi qua private server functions với request hash/idempotency.

Client integrity/obfuscation chỉ là moderate tamper signal/IP hardening. Patched client không được ngăn mở project local; nó cũng
không thể tự tạo cloud entitlement/credit/package URL. Đây là kiến trúc crack-resistance chính.

## Privilege remediation

Runtime file-only không ghi Program Files/HKLM, không install driver/service/game content và không cần high integrity. PLAN 90 đổi
manifest sang `asInvoker`, giữ `uiAccess=false`, không thêm `runas`, self-relaunch hoặc broker. Các gate được chạy từ token:

- Windows user: không thuộc Administrators;
- integrity: Medium Mandatory Level;
- App Debug/Release + XAML;
- PostgreSQL, ACV Tool 5, DirectXTex và Product Gate C.

Upgrade từ một bản phát hành elevated thật, alternate-admin historical profile và installer per-user/per-machine cần release QA
riêng. App không auto-discover/copy/take ownership dữ liệu của profile khác.

## Public release exposure gate

`Protect-ReleaseArtifactExposure.ps1` chỉ được chuẩn bị trên layout có exact App executable sentinel. `-Prepare` xóa PDB bên
dưới exact artifact root; gate sau đó reject debug symbols, C# source/project/solution/user files, private signing key material,
obfuscation maps và known proprietary fixtures. Release workflow chạy gate trước signing rồi chạy secret scan sau signing.

Không dùng việc bỏ PDB để tuyên bố managed code không decompile được. Private symbol retention cho crash analysis phải nằm ngoài
public artifact, có ACL/retention/access audit của release operations; repository chưa production-verify kho private đó.

## Quyết định paid beta

Repository/internal manual gate: `PASS`. Paid beta production release: `NO-GO` cho tới khi có deployment evidence tối thiểu:

1. signed/timestamped app + installer và protected signing key/HSM;
2. signed updater manifest/package, exact live CDN/origin và E2E install/rollback;
3. approved Supabase staging/live RLS/role/network test và TLS/WAF/distributed rate limiting;
4. live Stripe/payment operational verification nếu payment được bật;
5. written redistribution rights/provenance và giải quyết SBOM `NOASSERTION` blockers;
6. security-owner sign-off; independent commissioned review nếu commercial risk owner yêu cầu.

Không có mục nào trong review mở rộng product sang game discovery/install/patch/launch/process hoặc runtime/in-game automation.
