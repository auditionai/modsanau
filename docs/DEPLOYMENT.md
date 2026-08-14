# Checklist deployment Netlify

Tài liệu này là cấu hình dự kiến; không xác nhận dashboard đã được cấu hình.

1. Kết nối đúng public repository sau khi chủ sở hữu xác minh owner và URL.
2. Base directory: để trống nếu repository root là cây này.
3. Build command: `npm run build`.
4. Publish directory: `dist`.
5. Production branch: `main`.
6. Bật branch deploy cho `develop`; không promote deploy này sang production trong PLAN 103.
7. Không thêm Supabase service-role key, AI key, signing key hoặc secret đặc quyền vào site/env.
8. Chạy `npm run check` trước push; xác minh deploy log và branch URL sau push.
9. Chỉ đưa ZIP vào GitHub Release sau legal/provenance, secret scan, signing và release gate riêng; không commit ZIP vào repository.

Site mục tiêu do Product Owner cung cấp: `https://modsanau.netlify.app/`.
