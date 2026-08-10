# Nền tảng bảo mật

Tài liệu bắt buộc chi tiết là `Audition_AI_Mod_Studio_SECURITY_CHECKLIST_V2.md`. File này ghi lại các quyết định nền tảng áp dụng từ PLAN 01.

## Trust boundary

Windows client và dữ liệu local không phải nguồn tin cậy cho hoạt động thương mại. Trusted backend phải quyết định entitlement, AI pricing, credit reservation/charge/refund và trạng thái payment.

Không được đưa vào client, source control, log hoặc crash report:

- AI provider secret;
- Supabase service-role key;
- payment/webhook secret;
- template master encryption key;
- private signing key.

Token người dùng cần lưu về sau phải đi qua Windows secure storage abstraction. Obfuscation, Native AOT, anti-tamper, hidden directory, đổi extension và keydat chỉ là hardening layer.

## File và process boundary

- Không sửa pristine/global archive template.
- Không commit commercial template, `acv.exe`, keydat hoặc extracted game assets.
- Project chỉ thao tác trên working copy trong validated isolated workspace.
- Validate và canonicalize untrusted path; chặn path traversal.
- Launch tool/native component bằng absolute trusted path; không ghép shell command từ input.
- ACV Tool 5 về sau phải redirect stdin/stdout/stderr và kiểm tra tool integrity.

## Release boundary

Release cuối cùng phải có signed binary, signed installer và signed/hash-verified update. Signing key nằm trong secure CI/HSM/certificate service, không nằm trong repository hoặc client.

Các biện pháp RLS, credit concurrency, payment webhook, template encryption, signing và supply-chain automation chỉ được triển khai ở PLAN được roadmap quy định. PLAN 01 không cung cấp implementation giả hoặc security theater.

## Elevation và logging từ PLAN 02

- Executable dùng Windows manifest chuẩn với `requestedExecutionLevel="requireAdministrator"` và `uiAccess="false"`.
- Không có self-relaunch, `cmd.exe`, PowerShell, giả lập hoặc bypass UAC.
- Toàn ứng dụng hiện chạy elevated theo yêu cầu sản phẩm; điều này làm tăng blast radius. Business/domain contract không phụ thuộc elevation để có thể tách `normal UI + elevated broker` về sau.
- Normal application data chỉ nằm dưới `%LocalAppData%\AuditionModStudio`, không nằm trong installation directory.
- Log rolling file có timestamp, level, structured properties và exception stack trace.
- Source code không được ghi password, token, API/payment/encryption/signing secret vào log. Thông báo lỗi cho người dùng không hiển thị stack trace.

## Path boundary từ PLAN 03

- Relative path không được nối trực tiếp. `IPathSecurity` bắt buộc canonicalize rồi xác nhận kết quả còn dưới approved root.
- Cấm `..`, `.`, absolute path, UNC path, drive switching, empty segment, Windows reserved name, trailing dot/space, alternate data stream và malformed filename.
- Unicode filename hợp lệ được chuẩn hóa Form C; khoảng trắng và tiếng Việt vẫn được hỗ trợ.
- Startup từ chối reparse point đã tồn tại trong managed path trước khi logger ghi file. Workspace create/resolve/cleanup kiểm tra lại reparse point; traversal lúc cleanup dùng top-level enumeration để không đi xuyên symbolic link/junction.
- Đây là mitigation thực dụng, không phải filesystem sandbox tuyệt đối. Validate-then-use vẫn có cửa sổ TOCTOU nếu local attacker có thể thay path đồng thời. Các operation nhạy cảm về sau phải kiểm tra lại ngay trước khi mở file; broker tương lai nên dùng handle-based/open-reparse-point policy và ACL chặt hơn.

## Administrator và LocalApplicationData

Policy PLAN 03 là application data thuộc Windows identity đang chạy process. `LocalApplicationData` là known folder per-user của current non-roaming user.

- UAC consent bằng cùng administrator account: elevated process vẫn thuộc cùng identity, nên root vẫn là LocalAppData của tài khoản đó.
- UAC credential prompt dùng administrator account khác: process elevated chạy bằng alternate credentials; LocalAppData có thể thuộc profile của administrator đó, không phải tài khoản đang sở hữu desktop ban đầu. Ứng dụng không tự đoán hoặc hard-code profile của desktop user.

Hệ quả hiện tại là project/log/settings có thể xuất hiện trong profile của alternate administrator. Đây là technical debt do yêu cầu toàn app `requireAdministrator`. Hướng dài hạn là UI chạy unelevated và chỉ privileged operation đi qua elevated broker có protocol/path allowlist; PLAN 03 không thay đổi yêu cầu elevation.
