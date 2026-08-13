# Process launch và DLL search hardening

## Inventory PLAN 81

Runtime product có đúng hai external-process launch sites:

| Site | Identity authority | Working directory | Pre-launch integrity | Shell/arguments |
|---|---|---|---|---|
| `AcvTool5Runner` | `TrustedArchiveToolManifest` / `ArchiveToolIds.AcvTool5` | managed randomized workspace vì ACV cần relative archive/keydat semantics | source, copy và ngay trước launch; exact filename + SHA-256 + no-reparse + không DLL ngoài package | no shell; `ArgumentList`; redirected stdin/stdout/stderr |
| `DirectXTexEvaluationHarness` | `DirectXTexEvaluationToolCatalog.May2026X64` | isolated `DirectXTexEvaluation` tool directory | source/copy và re-hash/no-reparse/exact-directory inventory ngay trước launch | no shell; `ArgumentList`; stdout/stderr redirect; `--` trước input |

Các `Process.Start` còn lại chỉ nằm trong automated test/release tooling để chạy PowerShell policy script hoặc test build;
không thuộc application runtime. Runtime không gọi `cmd.exe`, PowerShell, `ShellExecute`, `SendKeys` hoặc raw `Arguments`.

Native imports là tên Windows system DLL code-owned: `kernel32.dll`, `advapi32.dll`, `crypt32.dll`, `wintrust.dll` và
`user32.dll`. Không có `NativeLibrary.Load`, `LoadLibrary` hoặc tên DLL từ project/user. SkiaSharp là package-native
dependency và phải nằm trong signed application layout; không được resolve từ workspace/project/export/Downloads.

## Policy

`WindowsProcessLaunchHardening.ApplyProcessDllPolicy` được gọi trước XAML initialization và trước mọi child start:

- `SetDllDirectory("")` loại current directory khỏi standard DLL search và áp dụng cho unpackaged child process;
- `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_APPLICATION_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32)` giới hạn app process;
- không bật `LOAD_LIBRARY_SEARCH_USER_DIRS`, không gọi `AddDllDirectory` và không thêm workspace/temp/project/export.

Mỗi child nhận exact canonical absolute executable và working-directory path, `UseShellExecute=false`, empty raw
`Arguments`, typed `ArgumentList`, no-window và streams theo protocol. Environment không kế thừa wholesale: chỉ
`SystemRoot`, `WINDIR`, `SystemDrive`, system-only `PATH`, cùng controlled `TEMP`/`TMP`. AI/provider/database/auth/signing
environment không được forward hoặc log. Executable không bao giờ được resolve bằng `PATH`.

ACV cần workspace làm current directory vì protocol `acv -da/-ca <archive> <folder>` và keydat semantics. Current
directory đã bị loại khỏi child DLL search; `acv.exe` standalone policy reject mọi `.dll` cạnh executable. DirectXTex
tool directory phải chứa đúng verified `texconv.exe` ở thời điểm pre-launch; unknown companion/subdirectory làm fail.
Nếu vendor package tương lai có companion thật, phải thêm exact filename/hash/package manifest bằng PLAN được duyệt;
không copy random DLL và không bundle Windows system DLL.

## Compatibility và residual risk

Microsoft ghi nhận `SetDefaultDllDirectories` chỉ tác động process gọi, còn `SetDllDirectory` của unpackaged parent tác
động child search order. Vì vậy cả process policy và sanitized child environment đều cần thiết:

- <https://learn.microsoft.com/windows/win32/api/libloaderapi/nf-libloaderapi-setdefaultdlldirectories>
- <https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-setdlldirectoryw>
- <https://learn.microsoft.com/windows/win32/dlls/dynamic-link-library-security>

Policy không phải sandbox. Same-user/Administrator có đủ quyền vẫn có thể race/replace file giữa final verification và
OS image open. Defense hiện có là randomized protected workspace, canonical/no-reparse path, code-owned package identity,
hash sát launch, restricted DLL search/environment và PLAN 80 monitoring. Không dùng anti-debug, kernel hook hoặc can thiệp
process khác. PLAN 90 đã chuyển runtime sang `asInvoker`; real ACV/DirectXTex gate PASS dưới medium-integrity token.

Real ACV Tool 5 và DirectXTex may2026 x64 gates là compatibility authority. Production packaging/signing/SBOM và exact
native dependency inventory của signed release vẫn `PRODUCTION NOT VERIFIED`.
