# Báo cáo BUILD — Admin Portal V2

## Đã build

- Login BFF email/mật khẩu, cookie admin và CSRF.
- Auto-bootstrap owner theo email cấu hình Gateway.
- Role gate owner/operator/auditor; auditor không thể mutation.
- Dashboard user/device/subscription/payment/gift code theo 7/30/90 ngày.
- Quản lý user, device, transaction, package, gift code, admin account và audit theo tab riêng.
- User/package dùng deactivate/archive, không xóa lịch sử tài chính append-only.
- Giao diện responsive mới, đồng nhất visual language landing page; không còn form API/token.

## Cấu hình production bắt buộc

- `Gateway__SupabaseUrl`
- `Gateway__SupabasePublishableKey`
- `Gateway__CreditDatabaseConnectionString`
- `Gateway__AdminPortal__BootstrapEmail=codycn2804@gmail.com`
- Gateway HTTPS và reverse proxy cùng origin cho `/v1/*`.
- Apply migration `202608150001_admin_portal_v2.sql`.
- Tạo hoặc xác nhận Supabase Auth user `codycn2804@gmail.com` qua kênh được ủy quyền; không commit mật khẩu.

## Ranh giới bằng chứng

Build/test local không chứng minh migration, Gateway, reverse proxy, MFA hoặc owner credential đã được
provision trên cloud. Không được báo portal đã tự kết nối dữ liệu live cho tới khi các gate production này PASS.

## Kết quả test

- Gateway: 137 PASS, 12 SKIP do PostgreSQL integration environment không được cấu hình.
- Public/admin static test: PASS.
- Public dist inventory và secret scan: PASS.
- Full solution: các suite Core/Imaging/Security/Projects/Gateway/DDS/Archives PASS; 13 test IntegrationTests
  báo dynamic-skip dưới dạng failure vì thiếu approved `texconv.exe`, không liên quan thay đổi admin.
- Supabase linked migration check trả HTTP 403 do tài khoản CLI hiện tại không có quyền project; migration và
  owner credential chưa thể provision từ workspace này.
- Public source inventory kiểm kê toàn bộ cây `web/` và các file build/deploy allowlist trong monorepo; dist
  tiếp tục được kiểm kê tuyệt đối sau build.

## Kết quả push/deploy develop

- Commit triển khai: `a6a21f1`; commit sửa inventory monorepo: `d9b7c76`.
- GitHub Actions `Public site CI` run `31821468525`: PASS.
- `https://develop--aumodstudio.netlify.app/admin/`: HTTP 200, Portal V2 đã live.
- `/v1/admin/session` và `/health` trên cùng origin: HTTP 404. Netlify chưa có reverse proxy/Gateway, vì vậy
  login và dữ liệu live chưa operational dù static UI đã deploy.
