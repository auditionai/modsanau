# Audition AI Mod Studio Application Installer — PLAN 95

## Quyết định packaging

Audition AI Mod Studio dùng **single-project MSIX x64, per-user** làm installer duy nhất. Quyết định này dựa trên
compatibility build thật của WinUI 3 project hiện hữu, không thêm WiX/NSIS/Inno/MSI/Squirrel/bootstrapper. MSIX phù hợp vì
repository đã có `EnableMsixTooling`, package manifest và Windows App SDK build tooling; deployment declarative không cần
custom action, service, driver, shell script hoặc runtime self-elevation.

Artifact phát hành có tên `AuditionAI-Mod-Studio-<four-part-version>-win-x64.msix`. Package identity ổn định là
`AuditionAIModStudio`, application ID là `App`, product display name là `Audition AI Mod Studio`. Source manifest dùng
publisher phát triển `CN=Audition AI Mod Studio Development`; build production bắt buộc inject exact X.500 subject và
publisher display name từ protected release environment. Development identity không phải production claim.

CLI Development build unsigned dùng namespace đặc biệt `CN=Unsigned` để Windows cho phép opt-in `-AllowUnsigned`; Visual
Studio/test-sign source manifest vẫn dùng `CN=Audition AI Mod Studio Development`. Production mode reject cả hai identity này.

Tài liệu Microsoft dùng để kiểm chứng quyết định:

- [Package your app using single-project MSIX](https://learn.microsoft.com/windows/apps/windows-app-sdk/single-project-msix)
  xác nhận `GenerateAppxPackageOnBuild=true` là command-line package gate.
- [MSIX containerization overview](https://learn.microsoft.com/windows/msix/msix-containerization-overview) mô tả full-trust
  WinUI desktop app chạy medium integrity, binary read-only, update/clean uninstall và user-created file preservation.
- [Understanding how packaged desktop apps run](https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)
  xác nhận package cài per-user vào package volume và writes ngoài package của full-trust app pass through theo quyền user.

## Version, prerequisite và installation scope

`New-AppInstallerPackage.ps1` chỉ nhận version Windows đúng bốn phần, từng component `0..65535`, rồi bind cùng giá trị vào
package identity, assembly version và file version. Manifest trung gian được sinh dưới `obj`, không sửa source manifest hoặc
để file generated trong source tree. Package build chỉ hỗ trợ Release/x64 và không claim x86/ARM64.

Ứng dụng tiếp tục .NET self-contained theo release model hiện hữu. Package khai báo dependency Windows App Runtime 2.3.1;
Windows package deployment kiểm prerequisite trước install. Installer không tự tải dependency, helper hoặc executable từ
URL. Production distribution/notice của exact framework dependency vẫn phải qua supply-chain/legal release review.

MSIX install per-user, không yêu cầu elevation mặc định và đặt binary trong package volume do Windows quản lý, thông thường
`Program Files\WindowsApps`. Runtime manifest riêng vẫn là `asInvoker`, `uiAccess=false`; package install không thêm
`runas`, self-relaunch hoặc broker. Start Menu identity do manifest tạo declaratively. PLAN 95 không thêm Desktop shortcut,
protocol handler hoặc file association cho `.audproj`, `.ab`, `.acv`.

## Upgrade, downgrade, repair và uninstall

Package identity giữ ổn định; version mới hơn được Windows deployment thay atomically. Package builder không dùng
`ForceUpdateFromAnyVersion`, nên older package không được silently overwrite newer package. PLAN 77 tiếp tục verify signed
manifest, exact URL/length/SHA-256/publisher/signature trước khi giao artifact cho installer boundary; channel, staged rollout,
deferral và rollback orchestration thuộc PLAN 96.

Repair/re-registration dùng Windows package deployment cho exact package; không có custom repair action. Failed install/update
không chạy privileged cleanup script và không mutate current known-good package. Uninstall chỉ gỡ package-owned binary,
registration và Start Menu entry. Không có recursive delete user-data action và không force reboot.

## Data preservation

Application data tiếp tục do `IAppPaths` quản lý dưới `%LocalAppData%\AuditionModStudio`, tách khỏi installation directory.
Full-trust packaged process chạy cùng Windows identity, nên settings, projects, logs, cache, backup và recovery workspace giữ
semantics hiện hữu. `.audproj`, user-selected project folder và exported `.ab/.acv` không thuộc package payload/ownership và
được giữ khi upgrade/uninstall.

Installer không đọc, copy, log, migrate hoặc xóa Windows Credential Manager entry. DPAPI encrypted premium cache tiếp tục
CurrentUser; per-user package không đổi thành machine-wide key scope. Alternate-admin profile không được discover/migrate.
Installer không cleanup active/recoverable workspace; PLAN 39/74 vẫn sở hữu recovery/cleanup có kiểm soát.

## Helper, legal và product boundary

Public MSIX fail nếu chứa `acv.exe`, `texconv.exe`, `015.ab`, `015.keydat`, `.ab/.acv`, `.audproj`, PDB, source, script,
private key, log, test, fixture hoặc workspace artifact. ACV Tool 5 redistribution vẫn
`BLOCKED_PENDING_WRITTEN_RIGHTS_AND_PROVENANCE_APPROVAL`; PLAN 95 không bundle/download tool. DirectXTex exact candidate có
technical provenance và MIT upstream nhưng release-package/notice review còn mở; MSIX hiện không bundle `texconv.exe`.

Installer chỉ cài Audition AI Mod Studio. Không detect/search game path/registry/launcher/process; không cài mod, copy export
vào game, backup/restore/patch/launch/login/control Audition hoặc tạo game shortcut/hook.

## Signing và release pipeline

Protected workflow build Release package với exact publisher, ký/timestamp các `AuditionModStudio.App.exe` và
`AuditionModStudio.*.dll` trước khi package block map được tạo, sau đó ký/timestamp MSIX bằng cùng PLAN 76 Authenticode
certificate. SignTool verify, PowerShell Authenticode verify, exact subject/thumbprint/timestamp và package scanner đều phải
PASS trước upload. Private key chỉ tồn tại trong protected certificate store/HSM/service; workflow chỉ nhận thumbprint.

Package scanner đọc content table/manifest trực tiếp, reject traversal/duplicate/private/proprietary/user artifact, kiểm exact
identity/version/x64/full-trust entry point và reject protocol/file association ngoài scope. Production thiếu signer,
publisher hoặc HTTPS RFC 3161 timestamp fail closed; không có unsigned production fallback. SHA-256 được tính trên exact final
bytes, sau signing; package không được sửa/repackage sau verify.

Development package có thể unsigned hoặc test-signed và luôn được báo rõ. Production certificate/HSM, exact publisher,
live timestamp, commercial redistribution và production install/update channel hiện **NOT VERIFIED**.

## Test và giới hạn

Automated policy tests bao phủ manifest/product identity, dynamic version, x64, runtime `asInvoker`, protected workflow,
missing production signer/publisher, content leakage, unsigned production rejection, no custom action, no helper/game/mod
packaging và data ownership separation. `Invoke-AppInstallerDeploymentTest.ps1` thực hiện opt-in per-user
old-version install → sentinel user data → upgrade → launch → downgrade rejection → uninstall → preservation check; nó không
đụng game hoặc xóa broad user directory.

Máy kiểm chứng PLAN 95 chạy standard-user và không có machine-trusted development certificate; Windows yêu cầu trust ở
LocalMachine Trusted People cho MSIX self-signed. Vì không được vượt UAC, actual install/upgrade/launch/uninstall gate đã dừng
an toàn trước mutation và được báo `NOT EXECUTED IN THIS ENVIRONMENT`. Development test-sign artifact được kiểm exact embedded
signature/signer và tamper rejection; điều này không được gọi là production signing hoặc deployment verification.

MSIX repair UI, Store distribution, enterprise deployment, Windows Server/MSIX Core, x86/ARM64 và migration từ một installer
production cũ không thuộc PLAN 95. PLAN kế tiếp là **PLAN 96 — Audition AI Mod Studio Application Updater**.
