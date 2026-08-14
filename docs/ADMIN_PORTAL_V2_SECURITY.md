# Bảo mật Admin Portal V2

> Kiến trúc deploy hiện hành đã chuyển sang Netlify + Supabase Auth/Data API theo
> `ADMIN_PORTAL_NETLIFY_SUPABASE.md`. Nội dung Gateway trong tài liệu này mô tả implementation V2 ban đầu.

## Session và xác thực

- Portal không nhận Gateway URL hoặc bearer token từ người vận hành.
- Email/mật khẩu chỉ đi qua HTTPS tới `POST /v1/admin/session/login`.
- Gateway xác minh credential với exact Supabase Auth origin rồi kiểm tra `private.admin_users`.
- Cookie `__Host-aams-admin` được ký bằng ASP.NET Data Protection, có `HttpOnly`, `Secure`,
  `SameSite=Strict`, path `/`, không persistent và hết hạn sau 30 phút.
- Access/refresh token Supabase không được trả về JavaScript và không được lưu trong web storage.
- Mọi mutation bằng cookie bắt buộc `X-CSRF-Token` 256-bit; so sánh constant-time.
- Login có rate limit theo IP và dùng lỗi chung để hạn chế enumeration.

## Phân quyền

- `owner`: toàn quyền business và quản lý tài khoản admin.
- `operator`: mutation business, không quản lý admin.
- `auditor`: chỉ đọc.
- Không cho tự vô hiệu hóa owner hiện tại hoặc loại bỏ owner active cuối cùng.
- Mọi mutation quan trọng có reason, correlation ID và audit event phía server.

## Bảo toàn dữ liệu

User được suspend/deactivate; package được archive. Không hard-delete payment, credit ledger hoặc audit trail.
Payment/credit authority vẫn nằm phía Gateway/database, không nằm ở portal.

## Gate production

Netlify chỉ host static portal. Production phải reverse proxy `/v1/*` tới Gateway cùng origin và cấu hình
Supabase/database/bootstrap email trong secret manager phía backend. MFA state đã có trong mô hình quyền,
nhưng enroll/challenge MFA phải được kiểm chứng với Supabase Auth hosted trước khi tuyên bố MFA operational.
