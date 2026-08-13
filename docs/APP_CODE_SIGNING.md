# App code signing

## Phạm vi và packaging

PLAN 76 áp dụng cho artifact phát hành của chính Audition AI Mod Studio. Repository hỗ trợ WinUI 3
single-project MSIX tooling và publish self-contained x64. PLAN 95 đã chọn một installer MSIX per-user; protected workflow
ký app-owned EXE/DLL trước khi đóng package, rồi ký/verify exact MSIX. Script signing nhận danh sách artifact explicit, chỉ
cho `.exe`, `.dll`, `.msix`, `.msixbundle`, `.msi`; `.ab` và
`.acv` không bao giờ dùng app code-signing key.

Debug/local build mặc định unsigned. Production release dùng workflow `Protected release signing`, chạy thủ công chỉ
từ `master`, trong GitHub environment `production-signing` và runner Windows riêng có nhãn `audition-signing`.
Environment phải giới hạn branch, required reviewer, cấm self-review/bypass nếu gói GitHub hỗ trợ. Runner phải ephemeral
hoặc được reset/kiểm soát chặt; environment không biến self-hosted runner thành sandbox.

## Authority và cấu hình

Private key không được export vào repository, runner workspace, environment variable hay artifact. Runner chỉ truy cập
certificate đã provision trong Windows `My` certificate store, ưu tiên key do HSM/managed certificate service bảo vệ.
Workflow nhận:

- secret `AUDITION_SIGNING_CERTIFICATE_SHA1`: exact SHA-1 certificate thumbprint dùng để select certificate, không phải key;
- variable `AUDITION_SIGNING_PUBLISHER_SUBJECT`: exact X.500 subject đã được release/security owner phê duyệt;
- variable `AUDITION_TIMESTAMP_URL`: HTTPS RFC 3161 timestamp endpoint;
- variable `AUDITION_SIGNTOOL_PATH`: absolute path tới Windows SDK `signtool.exe` đã được runner image quản lý.

`CN=Audition AI Mod Studio Development` trong `Package.appxmanifest` là development identity, không phải production
publisher. Khi phát hành MSIX, generated manifest thay Identity Publisher bằng exact subject của production certificate;
pipeline phải fail nếu không khớp. Không đặt fake production CN vào repository.

App code-signing key tách biệt với entitlement ES256 key, premium-template manifest key, payment/provider secrets và
update-manifest key của PLAN 77. Authenticode không cấp entitlement và không xác thực update metadata độc lập.

## Sign và verify

Pipeline publish exact version/commit trước, rồi ký app-owned EXE/DLL/package ở stage cuối bằng SHA-256 và RFC 3161
timestamp SHA-256. Mọi lỗi sign/timestamp/verify dừng release; không có unsigned fallback. Sau ký, pipeline chạy SignTool
với Authenticode policy và kiểm tra lại PowerShell Authenticode status, exact subject, exact thumbprint và timestamp
certificate. Report chỉ chứa filename, post-sign SHA-256 và public certificate metadata; không chứa workspace path hay key.

Không sửa, rename, repackage hoặc chèn resource sau verify. Artifact publish phải chính là bytes đã verify. Signing
success không được suy ra chỉ từ exit code của lệnh sign.

## Rotation, expiry và revocation

- Theo dõi expiry tối thiểu 90 ngày; xin certificate mới trước hạn và thử trên protected staging runner.
- Rollover giữ publisher identity continuity khi CA/policy cho phép. Cho phép đồng thời old/new thumbprint trong một
  cửa sổ release được phê duyệt, nhưng mỗi run chỉ nhận đúng một expected thumbprint.
- Sau rollover, cập nhật protected configuration và verifier/update policy theo release có audit; không reuse key của trust domain khác.
- Khi nghi lộ key: dừng signing environment, revoke certificate với CA/service, giữ artifact/hash/audit evidence, phát
  hành certificate mới, cập nhật allowlist và thông báo incident. Không xóa lịch sử để che incident.
- Timestamp hợp lệ giúp chữ ký đã tạo trong thời gian certificate hợp lệ tiếp tục có thể xác minh sau expiry; revocation
  và platform policy vẫn có thể làm artifact không còn trusted.

## Trạng thái xác minh

- Kiến trúc fail-closed và negative verification: có automated test.
- Dev signing: chưa cấu hình/chưa xác minh bằng certificate local.
- Production certificate signing: chưa xác minh; repository không có production certificate/service.
- Live RFC 3161 timestamp: chưa xác minh.
- Installer MSIX Development unsigned: compatibility build và content scanner đã xác minh ở PLAN 95.
- Installer/MSIX production signing: kiến trúc fail-closed đã nối CI nhưng chưa xác minh vì certificate/HSM, exact publisher
  và live timestamp production chưa được cấu hình.
