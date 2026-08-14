# Triển khai PLAN 106 — tài khoản, tải ứng dụng và thiết bị

## 1. Chạy migration Supabase

Trong Supabase Dashboard → SQL Editor, chạy các migration theo thứ tự, kết thúc bằng:

`supabase/migrations/202608150003_plan106_public_auth_device_download.sql`

Migration cuối tạo trigger hồ sơ, RPC `desktop_access_api`, bucket riêng tư
`desktop-releases` và policy chỉ cho tài khoản đã xác minh tải đúng artifact stable.

## 2. Cấu hình Supabase Auth

- Authentication → Providers → Email: bật Email provider và Confirm email.
- Authentication → URL Configuration:
  - Site URL: URL production của website.
  - Redirect URLs: thêm URL production, deploy preview và branch `develop` cần kiểm thử.
- Không bật anonymous sign-in cho luồng tải ứng dụng.

## 3. Biến môi trường Netlify

Tạo đúng hai biến, không thêm dấu `=` vào Key:

- `SUPABASE_URL` = `https://plvuutsjwsawkkrmvigz.supabase.co`
- `SUPABASE_PUBLISHABLE_KEY` = publishable key của project

Chọn `All scopes` và cấp giá trị cho Production, Deploy Previews và Branch deploys.
Không đưa `service_role`, secret key hoặc database password vào website.

## 4. Phát hành bộ cài

Sau khi artifact Windows x64 đã vượt qua build, security scan và signing:

1. Supabase Dashboard → Storage → `desktop-releases`.
2. Tạo thư mục `stable`.
3. Upload đúng tên `AuditionAI-Mod-Studio-win-x64.zip`.
4. Giữ bucket ở chế độ Private.
5. Cập nhật `docs/RELEASE_STATUS.json` với hash SHA-256 và artifact đã phê duyệt.

Nếu chưa có artifact, tài khoản vẫn đăng ký/đăng nhập được nhưng nút tải sẽ báo
“Bộ cài chưa được phát hành”. Đây là trạng thái fail-closed có chủ đích.

## 5. Desktop

Bản production đã chứa Supabase URL và publishable key công khai nên người dùng cuối
không phải tự tạo biến môi trường. Biến `AUDITION_SUPABASE_URL` và
`AUDITION_SUPABASE_PUBLISHABLE_KEY` chỉ dùng để trỏ build phát triển sang project khác.
