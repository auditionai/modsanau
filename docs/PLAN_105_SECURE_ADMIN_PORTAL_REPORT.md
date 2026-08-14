# Báo cáo PLAN 105 — Secure Admin Portal

## Kết quả

Đã triển khai cổng quản trị bảo mật cho Audition AI Mod Studio theo phạm vi backend/ops, không chạm runtime game.

## Đã build

- Thêm migration Supabase `202608140002_plan105_admin_portal.sql`.
- Thêm Gateway Admin Portal service và endpoints `/v1/admin/*`.
- Thêm static admin site trong `admin-release/`.
- Thêm contract tests cho migration, endpoint, service và static portal.
- Cập nhật tài liệu kiến trúc/bảo mật admin portal.

## Route Gateway chính

- `GET /v1/admin/bootstrap/state`
- `POST /v1/admin/bootstrap`
- `GET /v1/admin/dashboard`
- `GET /v1/admin/devices`
- `GET /v1/admin/devices/{deviceProfileId}`
- `POST /v1/admin/devices/{deviceProfileId}/block`
- `POST /v1/admin/devices/{deviceProfileId}/unblock`
- `POST /v1/admin/devices/{deviceProfileId}/revoke`
- `POST /v1/admin/devices/{deviceProfileId}/subscription/extend`
- `POST /v1/admin/credits/grant`
- `GET/POST /v1/admin/gift-codes`
- `POST /v1/admin/gift-codes/{giftCodeId}/revoke`
- `GET /v1/admin/gift-codes/{giftCodeId}/redemptions`
- `GET /v1/admin/audit`

## Test đã chạy

- `dotnet build src/AuditionModStudio.Gateway/AuditionModStudio.Gateway.csproj -nologo` — PASS.
- `dotnet test tests/Gateway.Tests/Gateway.Tests.csproj -nologo` — PASS, 136 passed / 12 skipped.
- `npm test` trong `admin-release/` — PASS.
- `npm run build` trong `admin-release/` — PASS.

## Chưa triển khai ngoài local

Không push Git, không apply migration lên Supabase hosted và không deploy Netlify vì workspace hiện không có Git remote/authenticated dashboard trong phạm vi turn này. `admin-release/dist/` chỉ là artifact build local.

## Ghi chú vận hành

Portal mặc định dùng same-origin/reverse proxy với Gateway vì CSP đang khóa `connect-src 'self'`. Nếu cần host khác origin, phải thay đổi CSP có chủ đích và kiểm tra lại deploy dashboard.
