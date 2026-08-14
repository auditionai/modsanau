# Mô hình bảo mật Admin Portal

> Cập nhật triển khai: Admin Portal trên Netlify gọi Supabase Auth và RPC bảo mật trực tiếp; không cần host Gateway
> riêng. Boundary hiện hành được mô tả tại `ADMIN_PORTAL_NETLIFY_SUPABASE.md`.

Admin Portal là bề mặt vận hành tin cậy nhưng không phải security boundary tự thân. Boundary thật nằm ở Gateway, Supabase RLS và bảng/function trong schema `private`.

## Quyền truy cập

- Tất cả route `/v1/admin/*` yêu cầu bearer authentication qua Gateway auth hiện hữu.
- Admin authorization được kiểm tra trong `private.admin_users`: user phải `is_active = true`.
- Bootstrap admin đầu tiên chỉ hoạt động khi:
  - `Gateway:AdminPortal:BootstrapUserId` hoặc `Gateway:AdminPortal:BootstrapEmail` được cấu hình.
  - user hiện tại khớp cấu hình.
  - database chưa có admin khác, hoặc replay đúng chính admin đó.
- Bootstrap dùng RPC `private.admin_bootstrap_first_user` để thao tác atomic ở database.

## Database hardening

Migration PLAN 105:

- Tạo `private.admin_users` và `private.admin_audit_events`.
- Enable + force RLS trên cả hai bảng.
- Revoke quyền từ `PUBLIC`, `anon`, `authenticated`.
- Chỉ grant bảng/function cần thiết cho `service_role`.
- Ghi audit bằng `private.admin_audit_record`.

## Mutation và audit

Các thao tác sau được ghi audit:

- Bootstrap admin đầu tiên.
- Block/unblock/revoke device profile.
- Extend subscription.
- Grant credits qua ledger authority `private.credit_grant`.
- Create/revoke gift code.

Portal gửi correlation id và reason; Gateway normalize reason để tránh empty/độ dài quá mức. Không log token hoặc secret.

## Static portal hardening

`admin-release/web/_headers` đặt:

- CSP `default-src 'self'`.
- `frame-ancestors 'none'`.
- `connect-src 'self'`.
- `X-Robots-Tag: noindex, nofollow, noarchive`.
- Tắt camera/microphone/payment permission.

Nếu Gateway chạy khác origin, cần chủ động sửa CSP/reverse proxy thay vì mở rộng mặc định trong repo.
