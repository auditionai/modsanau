# Admin Portal — Netlify + Supabase

## Kiến trúc triển khai

Admin Portal không còn phụ thuộc một host Gateway ASP.NET riêng:

`Browser /admin -> Supabase Auth -> public.admin_portal_api() -> private schema`

Netlify Function `admin-config` chỉ đọc và trả về hai giá trị public để static frontend khởi tạo kết nối. Hàm không nhận,
không cần và không được trả Supabase secret/service-role key hoặc database password.

## Cấu hình Netlify Web

Trong `Site configuration -> Environment variables`, tạo đúng hai biến:

- `SUPABASE_URL`: project URL HTTPS, ví dụ `https://project-ref.supabase.co`.
- `SUPABASE_PUBLISHABLE_KEY`: publishable key dạng `sb_publishable_...`.

Sau khi lưu, chọn deploy lại nhánh `develop`. Route `/admin/config` được Netlify rewrite tới Function cùng origin.

## Cấu hình Supabase Web

1. Mở `SQL Editor` và chạy migration `202608150002_admin_portal_netlify_supabase.sql` sau các migration trước đó.
2. Xác nhận Auth user `codycn2804@gmail.com` đã tồn tại và email đã confirmed.
3. Đăng nhập `/admin` bằng mật khẩu của user đó. Nếu bảng `private.admin_users` còn trống, RPC tự bootstrap duy nhất
   email này thành `owner`.

## Kiểm soát bảo mật

- Actor luôn lấy từ `auth.uid()` của JWT Supabase, không lấy user id do trình duyệt tự khai báo.
- `anon` không có quyền execute RPC; chỉ role `authenticated` được gọi.
- Mỗi request kiểm tra `private.admin_users.is_active` và role `owner/operator/auditor`.
- Mutation từ `auditor` bị từ chối; quản lý admin chỉ dành cho `owner`; owner cuối cùng không thể bị hạ quyền/tắt.
- Dữ liệu trong schema `private` không được expose trực tiếp. RPC `SECURITY DEFINER` là bề mặt hẹp, có validation và audit.
- Access/refresh token chỉ giữ trong `sessionStorage`, bị xóa khi đăng xuất và không được log.
- CSP chỉ mở kết nối HTTPS tới `*.supabase.co`; trang vẫn noindex và không cho script bên thứ ba.

## Giới hạn vận hành

Migration phải được áp dụng trên hosted Supabase trước khi đăng nhập. Build repository không có quyền dashboard và không tự
đưa migration lên project khi thiếu Supabase access token. Không nhập secret hoặc mật khẩu vào source, commit hay chat.
