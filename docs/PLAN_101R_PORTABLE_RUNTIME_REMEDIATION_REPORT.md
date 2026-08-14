# PLAN 101-R — Portable WinUI Runtime Compatibility Remediation

Ngày kiểm tra: 2026-08-14  
Trạng thái: **BLOCKED**

Starting HEAD: `69c3688b8eab552badfb475237898a3c5b8c971f`  
Final HEAD: `69c3688b8eab552badfb475237898a3c5b8c971f`  
Commit: Không có  
Working tree: Còn thay đổi chưa commit của PLAN 101 và báo cáo/repro PLAN 101-R.

## Repro Matrix

Môi trường đã khử định danh:

- Windows 11 x64, build `26200.9168`.
- .NET SDK `10.0.302`, runtime `10.0.10`.
- Project minimal chỉ có `Window`, `Grid`, `TextBlock` — `Portable WinUI Test`.
- Unpackaged, `asInvoker`, `uiAccess=false`, .NET và Windows App SDK self-contained.
- Launch từ working directory khác publish; đã thử cả path ASCII và Unicode.

| Windows App SDK | Microsoft.UI.Xaml | Minimal build | Minimal publish | Minimal portable launch | Main app launch | Kết quả chính xác |
|---|---:|---|---|---|---|---|
| 2.3.1 | 3.2.3.0 | PASS, 0 warning/error | PASS | FAIL | FAIL | APPCRASH `0xc000027b`, offset `0x3a9c5d` |
| 2.3.x servicing mới hơn | — | N/A | N/A | N/A | N/A | NuGet stable không có bản 2.3.x mới hơn 2.3.1 |
| 2.2.0 | 3.2.2.0 | PASS, 0 warning/error | PASS | FAIL | Không chạy theo version policy | APPCRASH `0xc000027b`, offset `0x3ad79d` |

Phép phân tách bổ sung:

- Minimal 2.3.1 framework-dependent, tải `Microsoft.UI.Xaml.dll` từ Windows App Runtime 2.3.1 đã đăng ký: FAIL cùng `0xc000027b`, offset `0x3a9c5d`.
- Minimal 2.3.1 self-contained với PRI mặc định: FAIL cùng `0xc000027b`, offset `0x3a9c5d`.
- Minimal 2.2.0 self-contained ở path ASCII: FAIL cùng offset như path Unicode.

## Root Cause

Classification: **ENVIRONMENT_SPECIFIC**

Evidence:

1. Lỗi tái hiện trong project generic không chứa dependency/startup/service/resource của Audition AI Mod Studio; vì vậy không còn là bằng chứng cho `PROJECT_STARTUP_DEFECT`.
2. 2.3.1 và 2.2.0 đều fail trên cùng máy; không đủ bằng chứng để tuyên bố `FRAMEWORK_REGRESSION_CONFIRMED` ở riêng 2.3.x.
3. Framework-dependent 2.3.1 và self-contained 2.3.1 fail cùng module version/offset; lỗi không chỉ do thiếu file trong portable layout.
4. PRI mặc định, PRI alias thử nghiệm, ASCII path, Unicode path và working directory tách biệt đều cho cùng kết quả.
5. Tài liệu Microsoft mô tả `WindowsPackageType=None` + `WindowsAppSDKSelfContained=true` là cấu hình xcopy-deploy được hỗ trợ; kết quả local trái với hành vi mong đợi đó.

Phân loại này chỉ áp dụng cho môi trường kiểm tra hiện tại, không khẳng định lỗi nền tảng phổ quát. Cần chạy repro generic trên ít nhất một Windows 11 x64 sạch khác hoặc gửi upstream để phân biệt OS/runtime installation corruption với platform compatibility issue.

## Selected Remediation

Không thay dependency hoặc kiến trúc production.

- Không downgrade app chính sang 2.2.0 vì minimal 2.2.0 cũng FAIL.
- Không đổi sang MSIX/installer/framework UI khác vì chưa có phê duyệt Product Owner.
- Giữ yêu cầu ZIP → extract → EXE → UI; không hạ acceptance gate.
- Tạo repro generic tại `artifacts/plan-101r-repro`, không chứa source/asset/tool proprietary.
- Repro upstream: `artifacts/plan-101r-portable-winui-repro.zip`, 4.699 byte, 9 entry,
  SHA-256 `B71AED6965C3F9C085E5D532E4443B8D152FD7B93FF7575E0B573653321B7E51`.

## Dependency Impact

- `Directory.Packages.props` và lock file production không bị đổi bởi PLAN 101-R.
- Trusted signer chỉ được thêm vào `artifacts/plan-101r-repro/NuGet.Config` cô lập để kiểm tra các package 2.2 đã ký bằng certificate Microsoft cũ; policy NuGet repository không bị hạ.

## Security Impact

- Runtime test giữ `asInvoker`, `uiAccess=false`.
- Không installer, MSIX registration, self-registration, registry bootstrap, elevation, shell launcher hoặc arbitrary DLL path.
- Không thu MachineGuid, hardware/MAC/disk serial; repro không có dump, token, path định danh, asset/tool/archive riêng.

## Performance Impact

Không đo PLAN 100 vì không có SDK change production và app không vượt startup. Không có performance regression do PLAN 101-R đưa vào sản phẩm.

## Portable Result

- ZIP candidate: Không tạo.
- ZIP repro diagnostic (không phải product candidate): `plan-101r-portable-winui-repro.zip`, scanner/inventory PASS.
- Clean extract: Minimal đã thử ở path ASCII và Unicode; app chính vẫn bị chặn.
- Actual WinUI UI: Không hiển thị.
- Admin/runtime setup: Không yêu cầu trong các phép self-contained; gate vẫn FAIL.

## UI Screenshots

Không có screenshot hợp lệ vì không có cửa sổ WinUI nào xuất hiện. Không dùng mock hoặc generated image.

## Regression

Không chạy full production regression vì PLAN 101-R không chọn hoặc áp dụng dependency change production. Build/test gate PLAN 101 trước đó vẫn là bằng chứng gần nhất; chưa được tuyên bố lại cho working tree sau remediation report.

## Known Limitations

- Chỉ có một máy Windows 11 build `26200.9168`; chưa có clean-machine cross-check.
- Không có native stack trace/symbolication từ dump vì tránh thu artifact có thể mang dữ liệu máy riêng.
- Không thử preview/experimental, 2.4 hoặc nhiều version lịch sử vì ngoài ma trận được phê duyệt.
- Không kiểm tra MSIX hoặc framework UI khác vì ngoài phạm vi và cần Product Owner quyết định.

## Product Owner Action

**PROVIDE DEPLOYMENT DECISION.**

Ưu tiên trước khi thay deployment model: chạy `artifacts/plan-101r-repro` trên một máy/VM Windows 11 x64 sạch. Nếu máy sạch PASS, sửa môi trường hiện tại hoặc dùng clean QA host; nếu máy sạch cùng FAIL, gửi repro generic upstream kèm event detail đã khử định danh.

PLAN 102 không được triển khai.
