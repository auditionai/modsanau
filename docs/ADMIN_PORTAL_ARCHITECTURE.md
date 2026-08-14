# Kiến trúc Admin Portal

PLAN 105 bổ sung một cổng quản trị tách khỏi website public và tách khỏi ứng dụng desktop. Portal là static web app trong `admin-release/`, gọi Gateway qua `/v1/admin/*`.

## Thành phần

- `admin-release/`: static admin portal, build deny-by-default bằng `public-allowlist.json`.
- `src/AuditionModStudio.Gateway/Endpoints/AdminPortalEndpoints.cs`: endpoint quản trị yêu cầu bearer auth và rate limit authenticated.
- `src/AuditionModStudio.Gateway/Services/AdminPortal.cs`: service quản trị phía server, đọc/ghi Postgres qua service-role runtime của Gateway.
- `supabase/migrations/202608140002_plan105_admin_portal.sql`: bảng admin user, audit event và RPC bootstrap/audit.
- `tests/Gateway.Tests/Plan105AdminPortalContractTests.cs`: contract guard cho migration, endpoint, service và portal static.

## Luồng chính

1. Operator mở Admin Portal nội bộ.
2. Operator nhập Gateway origin và bearer token của Supabase user đã được phép.
3. Portal gọi `GET /v1/admin/bootstrap/state`.
4. Nếu chưa có admin và user hiện tại khớp cấu hình `Gateway:AdminPortal`, operator có thể gọi `POST /v1/admin/bootstrap`.
5. Sau khi có admin active, portal gọi các route dashboard, devices, gift codes và audit.
6. Mọi mutation quan trọng truyền correlation id, reason và được ghi vào `private.admin_audit_events`.

## Ranh giới

- Portal không chứa service-role key, AI secret, payment secret hoặc signing key.
- Portal không gọi Supabase trực tiếp; chỉ gọi Gateway.
- Portal không đụng game/runtime/launcher/registry; phạm vi vẫn là backend vận hành account/device/commercial entitlement.
- Portal mặc định được harden để deploy cùng origin hoặc sau reverse proxy. Header `_headers` đang đặt `connect-src 'self'`.

## Giao diện

Portal dùng dark ops dashboard, form label rõ ràng, dialog xác nhận destructive action, aria-live status, keyboard focus và reduced-motion. UI có thể hủy request hiện tại qua `AbortController`.
