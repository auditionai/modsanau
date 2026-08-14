# Kiến trúc cập nhật Portable

## Quyết định

PLAN 102 chọn **in-place replacement có rollback backup theo signed inventory**.

Side-by-side version directory không được chọn vì layout Portable V1 hiện không có bootstrap/launcher ổn định. Thêm launcher, switch pointer và migration cho mọi ZIP cũ sẽ tăng bề mặt lỗi mà không cần thiết. In-place phù hợp layout hiện tại khi updater chạy từ staging, app đã thoát, file cũ được backup và chỉ file app-owned trong signed inventory được thay/xóa.

## Boundary sản phẩm

Updater chỉ cập nhật Audition AI Mod Studio. Nó không có authority tìm, đọc registry, cài, patch, backup, launch hoặc điều khiển Audition. Local editor/build/export vẫn file-only và kết thúc ở standalone `.ab`/`.acv`.

`AuditionModStudio.Updater` là library UI-independent. `AuditionAI.Updater.exe` là process helper riêng, `asInvoker`, `uiAccess=false`, không installer/service/registry/UAC.

## Trust model và manifest

PLAN 102 tái sử dụng exact envelope/domain PLAN 77:

- envelope schema 1;
- ES256/P-256, IEEE P1363;
- domain `AUDITION_APP_UPDATE_MANIFEST_V1\0`;
- bounded public-key rollover;
- private signing key không nằm trong desktop repository/client.

Payload Portable dùng schema 3, không tạo signature system thứ hai. Chữ ký được xác minh trước khi tin product, channel, version, URL, release notes, policy, package hash/size hay inventory.

```json
{
  "SchemaVersion": 3,
  "Product": "AuditionAI.ModStudio",
  "Channel": "stable",
  "Version": "1.0.1",
  "MinimumSupportedVersion": "1.0.0",
  "PublishedAt": "2026-08-14T00:00:00Z",
  "UpdatePolicy": "optional",
  "Package": {
    "Url": "https://release.example/AuditionAI-Mod-Studio-1.0.1-win-x64.zip",
    "FileName": "AuditionAI-Mod-Studio-1.0.1-win-x64.zip",
    "Size": 123456,
    "Sha256": "...64 hex...",
    "Product": "AuditionAI.ModStudio",
    "Architecture": "win-x64",
    "Distribution": "portable",
    "Inventory": [
      { "Path": "AuditionModStudio.App.exe", "Length": 1, "Sha256": "..." },
      { "Path": "AuditionAI.Updater.exe", "Length": 1, "Sha256": "..." }
    ],
    "RemoveOwnedFiles": []
  },
  "ReleaseNotes": ["Sửa lỗi Build"]
}
```

V1 chỉ cho phép `stable`. Version mới hơn mới được stage; equal là current; lower bị reject. `optional|required` là policy typed, nhưng `required` không khóa local offline workflow.

## Check và download

Window được hiển thị trước; automatic check chỉ bắt đầu sau khi shell khởi tạo. Automatic cadence là 6 giờ; manual check bỏ qua cadence. Timestamp lần thử được ghi dưới update root và không chứa project/device identity.

Network dùng HTTPS 443, exact signed/fixed URI, host allowlist, redirect disabled, timeout bounded và tối đa ba lần cho transient response/I/O. Download stream 64 KiB vào `package.zip.partial`; không buffer ZIP vào memory. Exact length và SHA-256 phải khớp trước atomic rename sang `package.zip`. User có thể hủy download; không có cancel trong install critical section.

Production manifest URI/public key không được hard-code khi Product Owner chưa cung cấp. Default composition dùng `UnavailablePortableUpdateCoordinator`; local workflow vẫn hoạt động.

## Staging và ZIP security

Root dự kiến: `%LocalAppData%\AuditionModStudio\Updates\<version>\<operation-id>\`.

Mỗi operation có:

- `manifest.signed.json`;
- `package.zip.partial` chỉ trong download;
- `package.zip` sau verify;
- `extracted\`;
- `rollback\` khi install;
- `install-status.txt` sau handoff.

Stager reject absolute/drive/UNC/`..`, invalid segment, case-insensitive duplicate, symlink/reparse entry, unexpected/missing entry, sai length/hash, quá 4.096 entry hoặc expanded size trên 4 GiB. Mỗi destination được canonicalize dưới exact staging root và ancestor reparse bị reject. Free space được kiểm tra theo package + expansion + safety budget.

## Handoff và process hardening

App copy `AuditionAI.Updater.exe` đã nằm trong verified inventory ra operation root, re-hash bản copy và launch bằng absolute path, `UseShellExecute=false`, `ArgumentList` typed:

```text
--parent-pid
--staging-dir
--install-dir
--expected-version
--restart-exe AuditionModStudio.App.exe
```

Không chấp nhận arbitrary restart executable. Install directory xuất phát từ `AppContext.BaseDirectory`, không từ remote manifest. Updater phải chạy ngay tại staging directory đã truyền. Trust public key là embedded build input; thiếu key thì exit fail closed. Local E2E nhúng TEST public key, private TEST key chỉ sinh dưới `artifacts`.

## Replacement và rollback

Sau khi parent thoát, updater:

1. lấy exclusive file lock;
2. verify lại signed envelope, version, package hash/length và extracted inventory;
3. backup mọi app-owned destination sắp thay/xóa;
4. copy từ staging sang `.update-new`, move/replace từng file;
5. chỉ xóa file trong signed `RemoveOwnedFiles`;
6. re-hash full final inventory;
7. restart exact primary EXE.

Failure sau backup khôi phục exact file cũ và xóa file mới chưa tồn tại. Unknown file bên cạnh EXE được giữ nguyên. `%LocalAppData%`, Credential Manager, `.audproj`, project/export/source image và folder ngoài signed application inventory không bị xóa.

## UI, activity và local-first

Settings có section Cập nhật thật: current version, stable channel, status, last check, check/download/cancel/defer/restart. Progress và recovery text bằng tiếng Việt, live region polite, semantic icon + text, không dùng color-only. Surface dùng token Creative Daylight/Studio Night hiện có.

Lifecycle event đi qua bounded Activity Log, không ghi signed JSON, URL query, full path, hash, stack trace hay secret. Extract/Convert/Build đang queued/running thì defer. Editor còn state có thể Apply thì restart bị chặn và yêu cầu Apply/Lưu.

Offline/DNS/timeout/feed fail không hiển modal startup error và không chặn create/load/edit/Apply/Validate/Build/Export.

## Quy trình release bug-fix

1. Bump application/version updater.
2. Build Release x64 Portable và chạy gate.
3. Quét artifact; không PDB/source/test/key/PFX/fixture/log/workspace/`acv.exe`/`015.ab`/`015.keydat`.
4. Chuẩn bị release notes UTF-8.
5. Chạy `scripts/New-PortableUpdateRelease.ps1` với private signing key nằm ngoài repository.
6. Upload ZIP.
7. Tải lại ZIP, xác minh size/SHA-256 và scanner.
8. Publish `stable.manifest.signed.json` **sau cùng**.
9. Dùng client version cũ xác nhận detect/download/update.

Production private key phải nằm trong protected CI/HSM/certificate service. Script reject production private key nằm trong repository.

## Giới hạn và trạng thái production

- Kiến trúc/updater local: implemented và có deterministic E2E.
- Production update feed/CDN/public key build input: chưa cấu hình.
- Production signing key/HSM, Authenticode/timestamp và live deployment: chưa verified.
- Health gate V1 xác nhận process mới start qua marker trong E2E. Automatic rollback nếu WinUI mới crash sau process creation chưa được bật; rollback dữ liệu và marker bản cũ vẫn được giữ theo operation cho recovery có kiểm soát.
