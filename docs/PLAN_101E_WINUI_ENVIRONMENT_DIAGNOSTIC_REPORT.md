# PLAN 101-E — WinUI Environment Diagnostic

Status: `PORTABLE_RUNTIME_FIXED`

Production HEAD: `69c3688b8eab552badfb475237898a3c5b8c971f`

Commit: `NONE`

Thời điểm chốt kiểm chứng: 2026-08-14 (Asia/Saigon).

## 1. Environment

| Hạng mục | Kết quả |
|---|---|
| Windows | Windows 11 Pro 25H2 x64, build `26200.9168` (registry legacy vẫn ghi `Windows 10 Pro`) |
| Process architecture | x64 / AMD64 |
| PowerShell | 5.1.26100.9168 |
| .NET SDK | 10.0.302 |
| .NET Runtime | Microsoft.NETCore.App 10.0.10; WindowsDesktop.App 10.0.10; AspNetCore.App 10.0.10; ngoài ra có .NET 6.0.27 |
| MSBuild | `dotnet msbuild` 18.6.11.33009 |
| Windows SDK cài machine-wide | Không phát hiện `C:\Program Files (x86)\Windows Kits\10` |
| Visual Studio / Build Tools | Không phát hiện Visual Studio Installer, `vswhere.exe` hoặc `msbuild.exe` độc lập |
| Target của ứng dụng | `net10.0-windows10.0.26100.0`, `win-x64` |

Việc solution và các mẫu WinUI đều build/publish thành công chứng minh build hiện tại dùng .NET SDK cùng Windows SDK/App SDK targeting assets đã restore bằng NuGet; việc không có Visual Studio hoặc Windows Kits machine-wide không phải nguyên nhân launch fail.

Yêu cầu máy phát triển:

- .NET SDK 10.0.302 tương thích repository và các package/tooling đã restore.
- Quyền đọc/ghi repository và NuGet cache; cần mạng chỉ khi package chưa có trong cache.
- Không bắt buộc Visual Studio IDE hoặc Build Tools độc lập cho pipeline đã kiểm chứng.

Yêu cầu máy người dùng cho bản portable đã kiểm chứng:

- Windows x64 được hỗ trợ.
- Giải nén đầy đủ ZIP rồi chạy `AuditionModStudio.App.exe` với quyền người dùng thường.
- Không cần .NET SDK, .NET Runtime, Visual Studio, MSIX registration hoặc Windows App Runtime cài riêng vì output chứa .NET và Windows App SDK self-contained.
- Máy kiểm chứng đã có VC++ v14 x64 hợp lệ. Nếu một máy đích thực sự thiếu VC++ x64 thì phải dùng Redistributable chính thức; PLAN 101-E không cài hoặc nhúng trình cài hệ thống.

## 2. Visual C++ Runtime

- x64: `Installed=1`, version `v14.44.35211.00`, `Major=14`, `Minor=44`, `Bld=35211`, `Rbld=0`.
- x86 (chỉ thông tin): `Installed=1`, version `v14.29.30157.00`.
- `C:\Windows\System32\vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll`, `concrt140.dll`: đều tồn tại, PE x64, file version `14.44.35211.0`, chữ ký Microsoft hợp lệ.
- Phân loại: `VC_RUNTIME_OK`.

Không có bằng chứng VC++ Runtime bị thiếu, sai architecture, thiếu file, sai chữ ký hoặc hỏng.

## 3. Windows App Runtime

- Các package 1.5, 1.6, 1.7, 1.8 và 2.3.1 được phát hiện ở x64/x86; trạng thái package liên quan là `Ok`.
- Package đích: `Microsoft.WindowsAppRuntime.2_2.3.1.0_x64__8wekyb3d8bbwe`, architecture x64, `Status=Ok`.
- Các native file liên quan trong package x64 có chữ ký Microsoft hợp lệ.
- Bản production là unpackaged self-contained, nên runtime package đã đăng ký không phải nguồn runtime bắt buộc cho ZIP. Kết quả sample chính thức và ZIP tự chứa đều launch được.

Không có bằng chứng Windows App Runtime máy bị thiếu hoặc hỏng.

## 4. Self-Contained Publish Inventory

Bản chốt: `artifacts/plan-101e-portable/1.0.0-internal.101e/AuditionAI-Mod-Studio-1.0.0-internal.101e-win-x64.zip`.

| File | Bytes | Version | SHA-256 | Chữ ký |
|---|---:|---|---|---|
| `AuditionModStudio.App.exe` | 291,840 | 1.0.0.0 | `0FFF4A98D589E474A023C0E83F0CCDA2BD02E05BBC901F438FFCEEB89007B94E` | Chưa ký — internal QA |
| `AuditionModStudio.App.pri` | 2,246,064 | resource index | `27EE3603730E4EDD9372452ECCF753C743B09886B0DFC35A2F2669810CA0293E` | Không áp dụng |
| `Microsoft.UI.Xaml.dll` | 15,323,960 | 3.2.3.2607 | `F711DC80CAB7D48A057CA5FB0FE3C55D3FB55DBFA82BAA6B68195EA7DF827E81` | Valid |
| `Microsoft.WindowsAppRuntime.Bootstrap.dll` | 394,040 | 2.0 | `07F219D022F5B2FDC75F455D6DC7DF35EBDCFCB2DCD0A14C59827C64EEFC9B70` | Valid |
| `Microsoft.WindowsAppRuntime.dll` | 2,603,832 | 2.0 | `C0B35816A77E0E6A50172DBF39EC295238E56852F54786EC61581F5D998788B3` | Valid |

- Inventory: 558 file, 249,013,493 payload bytes.
- ZIP: 96,181,759 bytes; SHA-256 `AC37DF7EDE36E85974CBA82D35CCEF8FBCB519EC32D286014E1BBBB4F5A53B80`.
- Release policy: `PASS`; secret scan: `PASS`.
- PE import audit trên executable và native WinUI/App Runtime binary không phát hiện native import chưa được resolve trên máy kiểm chứng.
- Chuỗi ứng dụng, WinUI và native runtime đều là x64; không có x86/ARM64 lẫn vào process chain.
- Không thiếu file native/resource cần thiết theo deployment model đã chạy thực tế.

## 5. Official Microsoft Sample

- Source: `https://github.com/microsoft/WindowsAppSDK-Samples`.
- Revision: `18431c6d0111d6a4bd8d46b9c8a82add47ccb013`.
- Sample: `Samples/SelfContainedDeployment/cs2/cs-winui-unpackaged`.
- BUILD: `PASS`.
- PUBLISH: `PASS`.
- LAUNCH: `PASS`.
- WINDOW VISIBLE: `YES`.
- Một biến thể sample official với Windows App SDK 1.8 cũng launch và hiển thị cửa sổ.

Đối chiếu sample cho thấy môi trường có thể chạy WinUI 3 unpackaged. Với SDK 2.3, cấu hình kiểm chứng cần thống nhất `Platform=x64` và `EnableMsixTooling=true`; đây là khác biệt cấu hình, không phải lỗi runtime hệ điều hành.

## 6. Minimal Repro

Sau khi publish bằng `Platform=x64` và cấu hình tooling tương thích sample chính thức:

- BUILD: `PASS`.
- PUBLISH: `PASS`.
- LAUNCH: `PASS`.
- WINDOW VISIBLE: `YES`.

Trước sửa, Windows Error Reporting ghi:

- Exception code ngoài: `0xc000027b` (stowed exception).
- Fault module: `Microsoft.UI.Xaml.dll`; WER report chi tiết quy về `combase.dll`, offset `0x600a4`.
- HRESULT được WER nêu: `0x80004005 (E_FAIL)`.

Không có crash dump sẵn. WinDbg/cdb không được cài; PLAN không cho phép tự cài, vì vậy không thực hiện cài debugger hoặc bật dump machine-wide.

## 7. 0xc000027b Root Exception

Instrumentation managed tối thiểu bắt được exception thực tế:

- HRESULT: `0x802B000A`.
- Exception type: `Microsoft.UI.Xaml.Markup.XamlParseException`.
- Component đầu tiên có thể hành động: XAML/resource composition của ứng dụng, không phải native prerequisite.
- Các lỗi cụ thể lần lượt được cô lập:
  - `Failed to create a 'Windows.UI.Color' from the text '{StaticResource AmsSlate100}'` trong semantic color resources.
  - `Failed to assign to RangeBase.Minimum` và sau đó `Failed to assign to RangeBase.Value` ở các control dùng giá trị phân số trong `ImageEditorPage`.
  - Chính sách DLL search áp quá sớm, trước khi DI dựng toàn bộ cây XAML eager, làm WinUI self-contained không hoàn tất late module/resource composition.

Không có native call stack từ WinDbg vì debugger không được cài và không cần cài thêm sau khi exception managed đã chỉ ra đúng XAML, bản sửa nhỏ đã làm minimal/main portable chạy ổn định.

## 8. Windows Health

- `DISM /Online /Cleanup-Image /CheckHealth` trả về error 740 vì phiên terminal không chạy administrator.
- Không nâng quyền, không chạy `RestoreHealth`, không chạy `SFC /scannow`, không sửa registry hoặc system runtime.
- Các system DLL liên quan đã kiểm tra (`combase.dll`, `FrameworkUdk.System.dll`, `Windows.UI.dll`, `dcomp.dll`, `DWrite.dll`, `d2d1.dll`, `d3d11.dll`, `dxgi.dll`) đều tồn tại và có chữ ký Microsoft hợp lệ.
- Vì app/sample đã chạy được và không có bằng chứng corruption, không có cơ sở đề nghị repair Windows. Kết quả CheckHealth là `NOT COMPLETED — ADMIN REQUIRED`, không phải báo cáo corruption.

## 9. Root Cause Classification

`PROJECT_CONFIGURATION_DEFECT`

Các nguyên nhân ứng dụng đã chứng minh:

1. Pipeline portable không truyền `-p:Platform=x64` dù RID là `win-x64`.
2. Production áp chính sách DLL search trước khi WinUI hoàn tất khởi tạo các page/resource được DI dựng eager.
3. Semantic `<Color>` dùng `{StaticResource ...}` làm text conversion không hợp lệ ở runtime.
4. Một số `RangeBase` nhận giá trị phân số qua XAML trong giai đoạn object construction không ổn định.
5. Custom PRI naming che khuất resource layout mặc định của entry assembly.
6. Sau khi runtime đã hoạt động, `PlaceholderContent` được đặt tên ở `StackPanel` bên trong thay vì `Border` overlay, nên code collapse sai phần tử và che nội dung route thật.

Đây không phải `MISSING_VC_RUNTIME`, `MISSING_WINDOWS_APP_RUNTIME`, `ARCHITECTURE_MISMATCH`, `WINDOWS_COMPONENT_CORRUPTION` hoặc `PLATFORM_RUNTIME_FAILURE`.

## 10. Missing Software / Prerequisites

**MÁY ĐANG THIẾU: `NONE PROVEN`**

**MÁY KHÔNG CẦN CÀI:** thêm VC++ Redistributable trên máy kiểm chứng, Windows App Runtime, .NET Runtime/SDK cho người dùng cuối, Visual Studio/Build Tools, WinDbg để hoàn thành remediation.

Máy không có WinDbg và không có Windows SDK/Visual Studio machine-wide, nhưng các thành phần đó không phải runtime prerequisite của portable và không cần cài để sửa lỗi đã chứng minh.

## 11. Recommended Fix

Bản sửa tối thiểu đã áp dụng:

- Pipeline portable truyền `-p:Platform=x64`.
- Giữ nguyên strict DLL policy, nhưng áp sau khi DI đã dựng `MainWindow`/XAML pages và trước `Activate`; các child tool vẫn tự harden process độc lập.
- Dùng ARGB literal cho semantic `Color`, giữ nguyên giá trị màu của Default/Light/HighContrast.
- Gán các giá trị phân số cho slider/range trong code-behind sau `InitializeComponent`.
- Dùng PRI mặc định theo entry assembly (`AuditionModStudio.App.pri`).
- Collapse đúng `Border` placeholder khi route thật đã được gắn.
- Thêm contract/regression test cho platform, hardening order, semantic colors và placeholder overlay.

ADMIN REQUIRED FOR FIX: `NO`

PRODUCT OWNER APPROVAL REQUIRED: `NO` đối với correction phía project đã nằm trong PLAN 101-E được phê duyệt.

Không cài phần mềm, không sửa Windows, không sửa registry, không tắt bảo mật.

## 12. WinUI Decision

`KEEP WINUI / PROJECT CONFIG FIX`

Không có căn cứ kỹ thuật để chuyển WPF/Avalonia/WinForms/Electron hoặc quay lại MSIX.

## 13. Portable Status

ZIP → EXTRACT → EXE: `PASS`

Kiểm chứng trên chính ZIP có SHA-256 nêu ở mục 4:

- Extract sạch vào thư mục riêng: `PASS`.
- Entry point đúng: `AuditionModStudio.App.exe`.
- Process ID kiểm chứng: 15568.
- `HasExited=false`, `Responding=true`.
- Window title: `Audition AI Mod Studio`.
- UI Automation thấy `Create an Audition mod project` và `1 · Choose game`.
- File count sau giải nén: 558.
- Delta của `early-startup.log`: 0 byte.
- Không yêu cầu admin, installer hay MSIX registration.

## 14. Visual UI Status

ACTUAL WINDOW VISIBLE: `YES`

Ảnh thật được chụp từ cửa sổ ứng dụng WinUI, không phải ảnh tạo/giả lập:

- Home: `artifacts/plan-101e-actual-home-fixed.png`
- Project Workspace / Texture Grid khi chưa mở project: `artifacts/plan-101e-actual-projects.png`
- AI Studio: `artifacts/plan-101e-actual-ai-studio.png`
- Texture Editor / Crop & Resize: `artifacts/plan-101e-actual-image-editor.png`
- Before/After: `artifacts/plan-101e-actual-before-after.png`
- Build & Export: `artifacts/plan-101e-actual-build-export.png`
- Account: `artifacts/plan-101e-actual-account.png`
- Settings: `artifacts/plan-101e-actual-settings.png`

Các trạng thái texture/project có dữ liệu thật không được giả lập vì phiên acceptance này không mở một project fixture. Các route/surface hiện có đều đã được kiểm tra hiển thị; ảnh Project Workspace ghi đúng trạng thái chưa có project.

## 15. Working Tree

Working tree dirty có chủ đích và được bảo toàn; không reset, clean, checkout đè hoặc commit. Trạng thái tại thời điểm lập báo cáo:

```text
 M docs/ARCHITECTURE.md
 M docs/SECURITY.md
 M scripts/Protect-ReleaseArtifactExposure.ps1
 M src/AuditionModStudio.AI/packages.lock.json
 M src/AuditionModStudio.App/App.xaml
 M src/AuditionModStudio.App/App.xaml.cs
 M src/AuditionModStudio.App/AuditionModStudio.App.csproj
 M src/AuditionModStudio.App/Bootstrap/ApplicationBootstrapper.cs
 M src/AuditionModStudio.App/DesignSystem/SemanticTokens.xaml
 M src/AuditionModStudio.App/Editor/ImageEditorPage.xaml
 M src/AuditionModStudio.App/Editor/ImageEditorPage.xaml.cs
 M src/AuditionModStudio.App/Home/HomePage.xaml
 M src/AuditionModStudio.App/Home/HomeViewModel.cs
 M src/AuditionModStudio.App/MainPage.xaml
 M src/AuditionModStudio.App/MainPage.xaml.cs
 M src/AuditionModStudio.App/MainWindow.xaml
 M src/AuditionModStudio.App/MainWindow.xaml.cs
 M src/AuditionModStudio.App/Shell/AppRoute.cs
 M src/AuditionModStudio.App/Shell/AppShellViewModel.cs
 M src/AuditionModStudio.App/Workspace/ProjectWorkspacePage.xaml
 M src/AuditionModStudio.App/Workspace/ProjectWorkspaceViewModel.cs
 M src/AuditionModStudio.Archives/packages.lock.json
 M src/AuditionModStudio.Cloud/packages.lock.json
 M src/AuditionModStudio.Core/packages.lock.json
 M src/AuditionModStudio.Dds/packages.lock.json
 M src/AuditionModStudio.Imaging/packages.lock.json
 M src/AuditionModStudio.Infrastructure/packages.lock.json
 M src/AuditionModStudio.Mods/packages.lock.json
 M src/AuditionModStudio.Projects/packages.lock.json
 M src/AuditionModStudio.Security/packages.lock.json
 M src/AuditionModStudio.Updater/packages.lock.json
 M tests/IntegrationTests/AppShellViewModelTests.cs
 M tests/IntegrationTests/DesignSystemResourceTests.cs
 M tests/IntegrationTests/Plan41AppShellContractTests.cs
 M tests/IntegrationTests/Plan42HomeContractTests.cs
 M tests/IntegrationTests/ProcessLaunchHardeningContractTests.cs
 M tests/Security.Tests/ReleaseArtifactExposurePolicyTests.cs
?? Audition_AI_Mod_Studio_MASTER_ROADMAP_V4.md
?? docs/PLAN_101E_WINUI_ENVIRONMENT_DIAGNOSTIC_REPORT.md
?? docs/PLAN_101R_PORTABLE_RUNTIME_REMEDIATION_REPORT.md
?? docs/PLAN_101_PRODUCT_ACCEPTANCE_REPORT.md
?? scripts/New-PortableRelease.ps1
?? src/AuditionModStudio.App/Legal/
?? src/AuditionModStudio.App/Settings/
?? tests/IntegrationTests/Plan101ProductAcceptanceTests.cs
```

Test cuối:

- Nhóm hồi quy PLAN 101-E/UI: 21 passed, 0 failed, 0 skipped.
- Full solution đã chạy trước bước đóng gói cuối: Core 145; Dds 105; Projects 153; Gateway 131 passed/12 skipped; Archives 130 passed/1 skipped; Security 121; Imaging 262.
- Integration full: 272 passed, 1 skip chuẩn; 13 dynamic skip được test runner hiển thị dạng `$XunitDynamicSkip$` do thiếu `texconv.exe` đã phê duyệt và private fixtures của PLAN 14/15/50/55/98/100. Đây là external prerequisite của test tích hợp dữ liệu, không phải regression WinUI/portable.

## 16. Product Owner Action

Hành động duy nhất được đề nghị: **review báo cáo và bản ZIP internal QA của PLAN 101-E; dừng tại đây, không triển khai PLAN 102 cho đến khi Product Owner phê duyệt PLAN tiếp theo.**

