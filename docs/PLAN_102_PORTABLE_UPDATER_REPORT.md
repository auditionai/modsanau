# PLAN 102 — Portable Automatic Updater + Bug-Fix Release Channel

**Status:** PASS

**Starting HEAD:** `cb27ca13dc57a62833797926a3f404365447456c`

**Final HEAD / Commit:** commit chứa báo cáo này, subject `plan-102: portable automatic updater`. Hash được ghi trong báo cáo bàn giao vì Git commit không thể tự chứa hash content-addressed của chính nó.

**Working tree:** yêu cầu sạch sau commit.

## Architecture

Đã thêm `AuditionAI.Updater.exe` chuyên thay file sau khi main app thoát. Đây là helper portable `asInvoker`, `uiAccess=false`, không phải installer, service hay elevated helper. App vẫn mở window trước rồi mới kiểm tra update bất đồng bộ; feed lỗi/offline không chặn editor local.

`AuditionModStudio.Updater` không phụ thuộc UI và không trực tiếp khởi chạy process. Handoff đi qua adapter process ở composition boundary, giữ policy không cấp quyền game/process cho updater library. Production composition fail closed bằng `UnavailablePortableUpdateCoordinator` vì Product Owner chưa cung cấp endpoint/trust root.

## Update Manifest

Payload portable schema 3 mang exact product `AuditionAI.ModStudio`, channel `stable`, semantic/application version, minimum version, policy `optional|required`, HTTPS package URL, filename, size, SHA-256, product/architecture/distribution, full owned-file inventory, signed removal list và release notes. Equal version trả current; lower version bị reject; version malformed bị reject.

## Trust / Signature

Tái sử dụng envelope schema 1 và ES256/P-256 của PLAN 77 với domain `AUDITION_APP_UPDATE_MANIFEST_V1\0`; không tạo signature system thứ hai. Chữ ký được kiểm tra trước khi tin version, URL, hash, length, release notes hay policy. Client chỉ nhận public key tại build input; thiếu key thì updater fail closed. Production private key không nằm trong repository; release script chủ động reject key bên trong repository.

## Download

HTTPS 443, exact URI/host allowlist, timeout và tối đa ba retry transient. Response được đọc theo stream với buffer 64 KiB vào `package.zip.partial`, có cancellation và giới hạn kích thước; chỉ atomic-promote thành `package.zip` sau khi exact length và SHA-256 khớp. Stale operation quá 14 ngày được dọn theo root kiểm soát; current app không bị tác động khi download/verify thất bại.

## ZIP Security

Stager reject ZIP hỏng/truncated, absolute path, drive/UNC, `..`, duplicate không phân biệt hoa thường, symlink/reparse, entry ngoài signed inventory, thiếu primary EXE/updater, quá 4.096 entry hoặc expanded size quá 4 GiB. Mỗi destination được canonicalize dưới staging root; length/hash từng file phải khớp signed inventory.

## Staging

Mỗi operation nằm dưới `%LocalAppData%\AuditionModStudio\Updates\<version>\<operation-id>\`, tách biệt khỏi source/project/`.audproj`. Package không bao giờ được extract trực tiếp lên application đang chạy. Disk-space probe tính package, expansion và safety budget trước khi tải/extract.

## Updater Process

App copy rồi re-hash updater đã có trong signed inventory, dùng absolute executable path, `UseShellExecute=false`, typed `ArgumentList`, không CMD/PowerShell/batch/ShellExecute text. Arguments chỉ gồm parent PID, staging dir, install dir tin cậy từ `AppContext.BaseDirectory`, expected version và exact primary executable. Updater chờ parent thoát, khóa single-flight, verify toàn bộ trust boundary lần nữa rồi mới thay file.

## Replacement Strategy

Chọn in-place replacement có inventory backup. Side-by-side bị loại vì portable V1 chưa có stable launcher/switch pointer; thêm launcher và migration lúc này tăng độ phức tạp không cần thiết. Mỗi file mới được ghi qua `.update-new`; chỉ file app-owned trong signed inventory được thay hoặc xóa.

## Rollback

Trước replacement, updater backup exact destination sắp thay/xóa. Fault injection trong replacement và locked-file test đều phục hồi phiên bản cũ, xóa candidate không có authority và giữ user data. Final inventory được re-hash trước restart. Backup/operation được giữ bounded để recovery; không loop vô hạn khi file bị khóa.

## User Data Safety

Unknown file cạnh EXE được giữ nguyên. Updater không xóa `%LocalAppData%`, Credential Manager, `.audproj`, project, export, source image hay đường dẫn ngoài signed application inventory. E2E xác nhận `.audproj` đại diện còn nguyên sau update thật.

## Permission Handling

App probe quyền ghi trước handoff. Folder không ghi được trả lỗi tiếng Việt có hướng dẫn, không elevation/UAC và không partial replacement. Updater/runtime manifests đều giữ `asInvoker`, `uiAccess=false`.

## Build/Export Deferral

Update restart bị defer khi Extract/Convert/Build đang queued/running; các operation này bao phủ pipeline Apply/Pack/Export hiện tại. UI báo ứng dụng sẽ cập nhật sau khi tác vụ hoàn tất. Download vẫn có thể hủy trước critical install; không expose cancel nguy hiểm sau switch.

## Unsaved Work Protection

Trước handoff, Settings kiểm tra editor state qua `CanApply`. Nếu còn thay đổi có thể Apply/Lưu, restart bị chặn và người dùng được yêu cầu lưu hoặc hủy update; không có đường silently discard.

## Update UI

Đã thêm section Cài đặt → Cập nhật, notification nhẹ ở shell và command palette “Kiểm tra cập nhật” chỉ khi service thật available. UI có current version, stable channel, trạng thái, lần kiểm tra gần nhất, release notes, check/update/defer/cancel/restart và progress live region. Evidence-only coordinator được loại khỏi public build bằng compile condition; public UI không giả trạng thái server.

Skill `ui-ux-pro-max` chỉ được áp dụng cho surface updater: giữ baseline Creative Daylight/Studio Night, bổ sung semantic state, keyboard/accessibility, progress và recovery copy; không redesign toàn ứng dụng.

## Vietnamese UX

Toàn bộ nhãn/trạng thái/lỗi updater hiển thị cho người dùng là tiếng Việt, không lộ raw manifest, full path, hash hay exception. Light/Dark dùng token hiện hữu, action có text rõ ràng và không truyền nghĩa chỉ bằng màu.

## Activity Log

Lifecycle check/available/download/verify/stage/defer/handoff/failure được ghi bằng structured, bounded, privacy-safe events. Log không chứa signed JSON, URL query, project filename, secret, stack trace hay nội dung DDS/archive.

## Local E2E Update

**OLD:** `1.0.0.0`

**NEW:** `1.0.1.0`

**Result:** VERIFIED / PASS. Run cuối tại `artifacts/plan-102-e2e-run4`: old process chạy từ đường dẫn có khoảng trắng và Unicode, nhận signed local fixture qua fake HTTPS transport, tải/verify/extract, handoff updater thật, thay binary thật, khởi chạy new process và nhận health marker `1.0.1.0`. User `.audproj` được xác nhận `PRESERVED`. TEST private key chỉ nằm trong ignored artifact/temp và được ghi nhãn NOT PRODUCTION.

## Performance

Đây là số đo thông tin trên máy acceptance ngày 2026-08-14, không phải SLA:

| Phép đo | Kết quả |
|---|---:|
| Startup portable khi không có production feed, đến window | 8.756 ms |
| Working set tại lúc window hiện | 193.175.552 byte |
| Background check local E2E | 11 ms |
| Verify envelope ES256 + parse manifest | 31.123,5 µs |
| Download/hash/extract/stage E2E fixture | 3.918 ms |
| Handoff launch updater single-file | 5.197 ms |
| Handoff đến new-version health marker | 24.352 ms |
| SHA-256 representative ZIP 135 MB | 217 ms |
| Extract representative ZIP | 10.518 ms |
| Streaming download buffer | 64 KiB cố định; không buffer ZIP 100+ MB vào RAM |

E2E cố ý giữ old app thêm 4 giây để kiểm tra wait-parent; số handoff-to-health bao gồm publish single-file startup và khoảng chờ này.

## Security

Threat review đã cập nhật cho manifest tampering/MITM/CDN compromise, rollback/downgrade, ZIP Slip, path/argument injection, DLL planting, staging tampering/TOCTOU, malicious local replacement và privilege escalation. Defense gồm signed trust trước metadata, HTTPS exact host, size/hash/inventory revalidation ở cả stager và updater, canonical path, reparse rejection, exact process path, single-flight lock, backup rollback, asInvoker và preservation theo ownership.

NuGet audit PASS không finding theo sources hiện tại. SBOM SPDX 2.3 được sinh và verify với 69 version package duy nhất. Secret scan PASS trên 605 file source/build/log/crash; artifact scan/secret scan PASS trên 328 file public payload. Fixture integrity: `015.ab` SHA-256 `3C4272BDDDA815B9F9E832D5FCF9EE10E2417301726C895CDB312DFF675C6081`; `015.keydat` `78F2910819D155CFBF0E366A80FEA4F40126BEA8C201E25EBDB0E0F956E2297F`; `acv.exe` `6A52C808D7E5A59EB41E43D86A32E78067F424C8531E34981887F093E81547D3`. `texconv.exe` và private DDS fixture không có nên các dynamic-skip liên quan được báo trung thực.

## Regression Totals

- App/Solution x64 Debug: PASS, 0 warning, 0 error.
- App/Solution x64 Release + XAML compile: PASS, 0 warning, 0 error.
- PLAN 102 updater/security targeted: 29/29 PASS, gồm cancel/timeout/partial stream/stale partial, oversized/missing EXE và deterministic crash checkpoints.
- PLAN 102 integration contracts: 5/5 PASS.
- Application updater policy: 7/7 PASS.
- Security full: 150/150 PASS.
- Final filtered executable suite: 1.355 PASS, 14 conditional skip chuẩn, 0 FAIL, exit code 0. Full unfiltered discovery có 1.360 test PASS và 14 skip chuẩn; 13 `$XunitDynamicSkip$` bổ sung bị runner biểu diễn thành FAIL vì không có approved `texconv.exe`/private fixtures PLAN 14/15/50/55/98/100. Filter chỉ loại sáu lớp fixture-dependent khỏi gate cuối, không sửa test hoặc acceptance criteria.
- Portable ZIP extract/launch từ working directory khác trong path Unicode: window visible + responsive PASS.
- File-only direction, asInvoker, no installer/MSIX/service/game authority: PASS.
- `dotnet format --verify-no-changes`, `git diff --check`, dependency audit, SBOM/lock, artifact scan và secret scan: PASS ở final gate.

## Screenshots

Actual WinUI evidence nằm trong ignored `artifacts/plan-102-ui-evidence/`:

1. `light/01-settings-no-update.png`
2. `light/02-update-available.png`
3. `light/03-download-progress.png`
4. `light/04-ready-to-restart.png`
5. `light/05-update-failure.png`
6. `light/06-update-success-after-restart.png`
7. `light/07-activity-log-update-events.png`
8. Studio Night: `dark/02-update-available.png`, `dark/03-download-progress.png`, `dark/04-ready-to-restart.png`, `dark/05-update-failure.png`, `dark/07-activity-log-update-events.png`.

Ảnh được capture từ actual WinUI bằng fixture compile-time nội bộ, không phải mockup; fixture không có trong public artifact.

## Release Artifact

- Filename: `AuditionAI-Mod-Studio-1.0.1-internal.102-win-x64.zip`
- Version: `1.0.1-internal.102`
- Size: `135.065.964` byte
- SHA-256: `2740B1DCC6EB52717F2F58AA3DED9DDB5172A9BA8247221E2A4410089066EC30`
- Inventory: 559 file; uncompressed payload 350.264.560 byte
- Signing state: Development/Internal QA portable ZIP chưa Authenticode/production-sign. Release-tool gate riêng tạo `1.0.1` với external TEST ES256 key, trạng thái `CREATED_NOT_PUBLISHED`, 559 inventory; đây không phải production signing.

## Production Status

- Updater architecture: **VERIFIED**
- Local E2E: **VERIFIED**
- Production update feed: **NOT VERIFIED**
- Production signing: **NOT VERIFIED**
- Production CDN: **NOT VERIFIED**
- GitHub Release integration: **NOT VERIFIED**

## Known Limitations

- Chưa có production manifest URI, public trust build input, private key/HSM, Authenticode/timestamp hay CDN live; production coordinator vì vậy cố ý unavailable.
- V1 xác nhận new process start/health marker trong E2E. Automatic rollback khi bản WinUI mới crash ngay sau `Process.Start` chưa bật vì chưa có reliable production shell-health acknowledgement; backup vẫn được giữ để recovery có kiểm soát.
- `required` là signed typed policy nhưng không khóa local editor/offline workflow.
- Các real DDS/archive fixture gate phụ thuộc `texconv.exe` đã phê duyệt/private fixture ngoài repository vẫn dynamic-skip trong môi trường hiện tại.

## Developer Bug-Fix Release Procedure

1. Bump version (ví dụ `1.0.0 → 1.0.1`) và cập nhật release notes UTF-8.
2. Build/test Release x64, XAML, updater/security/integration/E2E và regression gates.
3. Chạy `scripts/New-PortableRelease.ps1`; kiểm tra portable launch.
4. Chạy artifact exposure scan, secret scan, NuGet audit, SBOM/lock và format/diff gates.
5. Đặt production ES256 private key trong protected CI/HSM/service ngoài repository.
6. Chạy `scripts/New-PortableUpdateRelease.ps1` để tạo canonical ZIP, inventory, payload và signed envelope.
7. Upload ZIP lên HTTPS origin; tải lại và verify exact size/SHA-256/scanner.
8. Chỉ sau khi ZIP đã sẵn sàng, publish `stable.manifest.signed.json` **cuối cùng**.
9. Dùng client version cũ kiểm tra detect, download, verify, handoff, restart và health.
10. Theo dõi release; không đổi manifest sang package chưa upload và không dùng force downgrade.

## Product Direction

Sản phẩm cuối vẫn **portable**, **file-only**; pipeline chỉ build/export standalone `.ab`/`.acv`. PLAN 102 không detect/discover/mutate Audition installation, registry, launcher, game folder/process; không cài/patch/launch game và không bundle mới `acv.exe`, `015.ab`, `015.keydat` hay private Audition asset.

## Roadmap

Next: PLAN 103 — Marketing Landing Page + GitHub + Netlify.

PLAN 103 **không được triển khai** trong commit này. Dừng sau PLAN 102 và chờ Product Owner review.
