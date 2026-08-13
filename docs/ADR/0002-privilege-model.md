# ADR-0002: Privilege model cho file-only editor

- Trạng thái: Implemented ở PLAN 90; runtime manifest dùng `asInvoker`
- Ngày: 2026-08-12
- Chủ sở hữu: Product Architecture, Desktop Security, Release Engineering

## Quyết định

PLAN 90 đã hoàn tất migration từ `requestedExecutionLevel="requireAdministrator"` sang **unelevated desktop app
(`asInvoker`)** sau khi inventory không tìm thấy operation runtime nào cần elevation. Full Debug/Release, real ACV Tool 5,
real DirectXTex, App XAML/build và file-only Product Gate C đều PASS dưới Windows medium-integrity token không thuộc
Administrators. Installer/updater vẫn là boundary riêng và chưa production-verified.

Không implement broker chỉ để biện minh cho elevation. Nếu một operation tương lai thật sự cần privilege, nó phải có PLAN/ADR riêng và dùng least-privilege broker; UI/editor, network/auth, parsing, image/DDS, archive build trong owned workspace và export tới writable user-selected destination vẫn unelevated.

Không silent self-elevation, credential hopping, UAC bypass hoặc game-install authority.

## Inventory operation và privilege

| Operation | Path/resource | Cần Administrator? | Kết luận/evidence |
|---|---|---:|---|
| App settings, logs, projects, cache | Current-user LocalApplicationData | Không | User-owned managed paths từ PLAN 03 |
| Credential/session | Windows Credential Manager Generic Credential | Không | Current-user credential API |
| Template cache key wrapping | DPAPI current-user | Không | PLAN 72; elevation bằng alternate account còn làm sai user context |
| Protected workspace ACL | Owner + SYSTEM + Administrators protected DACL | Không | Owner có quyền đặt DACL trên directory vừa tạo |
| Import image/DDS/archive | File user chọn và có quyền đọc | Không | Không truy cập game installation |
| AI/auth/cloud | HTTPS | Không | User token; server giữ privileged authority |
| `acv.exe` extract/pack | App-approved binary + owned isolated workspace | Không có bằng chứng cần | Redirected child process, không install/patch game |
| DirectXTex decode/encode | App-approved binary + owned temp/workspace | Không | Không driver/service install |
| Project Apply/Validate/Build | Managed workspace | Không | User-owned local files |
| Standalone archive export | Exact user-selected writable destination | Không mặc định | Deny/offer another location khi ACL không cho ghi; không elevate để vượt ACL |
| App install/update | Installer-owned boundary | Có thể | Tách khỏi runtime app; signer/installer PLAN 76+ quyết định per-machine/per-user |
| Game detect/install/patch/launch | Không tồn tại | Không áp dụng | Absolute product non-goal |

Không có Registry HKLM write, service/driver installation, protected Program Files mutation, firewall/system configuration, process injection hoặc game-folder mutation trong runtime inventory.

## Rủi ro của toàn-app elevation đã được loại khỏi runtime

- Parser, native tool, image/archive input và UI đều chạy với high integrity, làm tăng blast radius của bug hoặc malicious file.
- Drag/drop, shell integration và IPC với unelevated process có thể bị hạn chế bởi integrity boundary.
- UAC credential prompt bằng alternate administrator làm LocalApplicationData, Credential Manager và DPAPI chuyển sang profile khác; project/session dường như “mất” dù vẫn nằm trong profile cũ.
- Child `acv.exe`/DirectXTex thừa hưởng elevation không cần thiết.
- Elevation không bảo vệ template/token khỏi Administrator; ngược lại làm attacker impact cao hơn.

## Migration sang unelevated app

1. PLAN 90 đổi manifest từ `requireAdministrator` sang `asInvoker`; PLAN 75 trước đó chỉ đưa ra quyết định.
2. Chạy compatibility matrix trên supported Windows/x64 với standard user, admin user không elevated và UAC alternate credentials.
3. Chứng minh read/write LocalApplicationData, Credential Manager, DPAPI cache, protected workspace, `acv.exe`, DirectXTex, Create/Open/Edit/Apply/Build/Export.
4. Export tới directory writable phải PASS; protected destination phải trả structured access-denied và không UAC prompt tự phát.
5. Installer/updater chọn per-user hoặc explicit per-machine elevation độc lập; runtime app không kế thừa installer privilege.
6. Sau gate, đổi manifest/test/docs trong cùng migration PLAN và cung cấp rollback release nếu compatibility regression.

## Upgrade và backward compatibility

- `.audproj`, workspace manifests, settings và encrypted cache schema không đổi; cùng Windows identity tiếp tục đọc LocalApplicationData/Credential Manager/DPAPI.
- Data được tạo khi app elevated bằng cùng account vẫn thuộc cùng profile và được đọc unelevated nếu ACL owner cho phép; migration test phải xác minh.
- Data vô tình nằm trong alternate administrator profile không được auto-discover, scan hoặc copy. UI tương lai chỉ có thể cung cấp explicit user-selected import/recovery sau path/integrity validation; không dùng credential của account khác.
- Existing protected DACL owner/SYSTEM/Administrators tương thích với unelevated owner. Entry có owner khác phải fail safely, không take ownership tự động.
- Rollback về release elevated không rewrite/downgrade schema và không silently move data.

## Least-privilege broker — chỉ nếu có nhu cầu tương lai được chứng minh

Broker phải là signed separate executable, khởi chạy bằng explicit UAC consent cho từng operation hoặc bounded session. Không chạy background service mặc định và không nhận raw command line từ UI.

Protocol đóng, versioned và authenticated theo process/session:

- Allowlist operation ID, ví dụ `InstallSignedAppUpdate`; không có `Run`, `Shell`, arbitrary executable/arguments, registry key, copy/move/delete generic.
- Request có correlation ID, schema version, bounded length và immutable typed fields; reject unknown JSON/member/enum.
- Broker tự resolve trusted executable/package metadata; UI không gửi storage credential, signing key hoặc executable path.
- Path allowlist chỉ app installation staging root do installer sở hữu và exact signed package; canonicalize, reject UNC/device path, ADS, traversal và reparse; ưu tiên validated handles để giảm TOCTOU.
- Response chỉ stable status/diagnostic, không raw exception, token, key hoặc sensitive path. Log operation ID/hash/result, không log secret/content.
- Timeout/cancellation/idempotency rõ; failure không để partial promoted state. Broker không bao giờ biết game path hoặc install mod/game.

## UAC model

- Mục tiêu runtime: không UAC prompt khi mở app hoặc xử lý project.
- Privileged installer/update tương lai: explicit Windows consent đúng thời điểm, `uiAccess=false`, không bypass và không lưu admin credential.
- User từ chối/cancel UAC: operation privileged fail/cancel; local editor tiếp tục hoạt động.
- Không auto-relaunch toàn app elevated sau access-denied export.

## Test plan và evidence migration

1. Static manifest `asInvoker`, `uiAccess=false`; không `runas`, self-relaunch, PowerShell/CMD elevation.
2. Standard-user smoke: startup/settings/log/auth/session/cache/workspace/create/open/edit/apply/build/export.
3. ACV Tool 5 và DirectXTex real fixture gates trong unelevated process.
4. DPAPI/Credential Manager roundtrip cùng identity trước/sau upgrade.
5. Existing elevated-era LocalApplicationData/project/cache/workspace compatibility.
6. Alternate-admin-profile data không auto-discover; explicit import only nếu được PLAN riêng phê duyệt.
7. Writable export PASS; protected/readonly/reparse/UNC/device path structured reject, không elevation prompt.
8. ACL owner/recovery/crash/concurrency và pristine non-mutation regression.
9. Malicious archive/image/path tests dưới standard-user token; no game path/registry/process behavior.
10. Nếu broker được phê duyệt: protocol fuzz/unknown-field/oversize/replay/cross-session/path allowlist/signature/UAC cancel/atomic rollback tests.

## Release gate và rollback

- Security owner và QA ký inventory + matrix; zero runtime operation còn phụ thuộc high integrity.
- App/installer signing (PLAN 76+) và updater design được phân biệt rõ runtime vs install privilege.
- Telemetry/log chỉ stable diagnostic, không token/path content; support runbook giải thích profile mismatch lịch sử.
- Nếu unelevated compatibility gate fail, giữ release elevated hiện tại và sửa abstraction; không thêm broad broker command hay silent elevation để đạt PASS.

## Non-scope

PLAN 90 không implement broker/installer/updater, không thêm UAC code và không cấp quyền game installation,
registry/game discovery, launch, patch, backup/restore hoặc runtime automation. Một số compatibility scenario lịch sử
(alternate-admin profile và upgrade từ bản phát hành thực tế) vẫn cần release QA, nhưng không phải lý do nâng toàn app.
