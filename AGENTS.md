# Hướng dẫn phát triển Audition AI Mod Studio

## Tài liệu có thẩm quyền

Trước mỗi PLAN, phải đọc:

1. `Audition_AI_Mod_Studio_MASTER_ROADMAP_V3.md`.
2. `Audition_AI_Mod_Studio_SECURITY_CHECKLIST_V2.md`.
3. `docs/ARCHITECTURE.md`.
4. `docs/SECURITY.md` và `docs/THREAT_MODEL.md` nếu đã tồn tại.
5. Trạng thái repository hiện tại.

## Quy tắc thực hiện

- Chỉ triển khai PLAN được người dùng phê duyệt.
- Quy trình bắt buộc: PLAN → BUILD → TEST → BÁO CÁO → DỪNG.
- Không tự chuyển sang PLAN tiếp theo.
- Không sửa module không liên quan hoặc làm yếu acceptance criteria/test để đạt PASS.
- Mọi trao đổi và tài liệu dự án viết bằng tiếng Việt; source code và định danh lập trình dùng tiếng Anh.
- Tác vụ dài phải bất đồng bộ, hỗ trợ hủy, báo tiến độ và logging khi PLAN tương ứng triển khai.
- Không nuốt exception; không log dữ liệu nhạy cảm.

## Ranh giới kiến trúc bắt buộc

- `AuditionModStudio.Core` không phụ thuộc UI, cloud hoặc implementation hạ tầng.
- UI không gọi `acv.exe` trực tiếp.
- Archive operation nằm sau `IAuditionArchiveService`/`IArchiveToolRunner`.
- DDS operation nằm sau `IDdsService`.
- Image operation nằm sau `IImageProcessingService`.
- AI operation nằm sau `IAiService` và trusted backend.
- Không hard-code extension archive; `.ab`, `.acv` và extension tương lai được biểu diễn bằng metadata/abstraction.
- Texture identity là normalized relative directory path cộng exact filename.
- Không bao giờ sửa pristine/global archive template; project luôn dùng working copy.

## Quy tắc ACV Tool 5

- Tool thực tế là `acv.exe`; fixture archive là `015.ab`.
- Extract: `acv -da 015.ab 015`; pack: `acv -ca 015.ab 015`.
- Khi thiếu `015.keydat`, AuditionVN phải được chọn bằng `"1"` qua redirected `StandardInput`.
- Redirect stdin/stdout/stderr, dùng absolute executable path, isolated working directory và structured arguments.
- Không dùng CMD hiển thị, `SendKeys`, giả lập input hoặc Windows UI Automation.
- `Select:` có thể không có newline; không chỉ dùng `ReadLineAsync()`.
- `writing :` biểu thị extract progress; `Packing:` biểu thị pack progress.
- Keydat là runtime companion artifact, không phải secret hoặc DRM boundary.

## Quy tắc DDS và bảo mật

- Fixture `tn_coby_logo.dds`: 6000×1801, DXT5/BC3, 1 mip.
- Không ép kích thước power-of-two.
- Không đưa AI secret, Supabase service-role key, payment secret, master encryption key hoặc private signing key vào client/repository.
- Credit và entitlement do server quyết định; client không phải nguồn tin cậy.
- Validate input/path, chặn path traversal và command injection.
- Dùng absolute path cho tool/native library; kiểm tra integrity của release artifact và companion tool theo PLAN.
- Obfuscation, Native AOT, hidden folder, đổi extension và keydat chỉ là hardening, không phải security boundary.
