# BÁO CÁO PLAN 104 — Hosted Supabase + Device Identity + Subscription/Entitlement

Ngày kiểm tra: 2026-08-14  
Nhánh: `develop`  
Hosted project ref dự kiến: `plvuutsjwsawkkrmvigz`

## Kết luận

Trạng thái tổng thể: **PARTIAL — local implementation PASS, hosted deployment NOT VERIFIED**.

Phần code, migration forward-only, Gateway, anonymous-auth client, secure grant cache, capability gates và Account UI đã hoàn thành. Môi trường hiện tại không có Supabase CLI, `SUPABASE_ACCESS_TOKEN`, database password, cấu hình runtime hay browser-control; repository cũng không có Git remote. Vì vậy không chạy migration hoặc smoke test trên hosted project và không tuyên bố hosted RLS/Auth đã PASS.

## Thiết kế đã triển khai

- First-run dùng Supabase Anonymous Auth; session/refresh token lưu bằng Windows Credential Manager.
- Device Code công khai `AMS-XXXX-XXXX-XXXX`, không tuần tự, không phải credential.
- Schema: device profiles, subscriptions, hashed Gift Codes, redemption, commercial audit event.
- Redeem duration gia hạn từ `max(server_now, current_expiry)`; redeem Credits gọi PLAN 60 `private.credit_grant`.
- Capability tập trung: AI, Build, Export, Premium Templates. AI và Build/Export đã nối gate thực thi.
- Gateway ký grant ES256 với header `AMS-ENT` v1; client chỉ giữ public key, xác minh chữ ký và cache grant trong Credential Manager đến đúng expiry.
- Account UI hiển thị Device Code, trạng thái/hạn/còn bao nhiêu ngày, Credits, quyền lợi; có copy, refresh, nhập/kích hoạt Gift Code và placeholder gia hạn minh bạch.

## Kết quả local

- `dotnet build AuditionModStudio.sln --no-restore`: PASS, 0 warning, 0 error.
- Core tests: PASS 147/147.
- Gateway tests: PASS 132, skip 12 test PostgreSQL yêu cầu database thật.
- Account/UI targeted tests: PASS 6/6.
- Full suite: các failure còn lại là dynamic-skip do thiếu approved `texconv.exe`/private fixture; trước khi cập nhật contract có 2 failure do thay đổi PLAN 104 và đã được cập nhật đúng acceptance mới.

## Hosted checklist bắt buộc

1. Trong Supabase Dashboard của đúng project, kiểm tra migration history và schema hiện tại; dừng nếu có object/data ngoài dự kiến.
2. Bật Anonymous Sign-Ins trong Authentication > Providers và cấu hình CAPTCHA/rate limits phù hợp theo tài liệu Supabase.
3. Cài Supabase CLI, đăng nhập bằng biến môi trường ngoài repository, `supabase link --project-ref plvuutsjwsawkkrmvigz`, rồi chạy `supabase db push --dry-run` trước `supabase db push`.
4. Xác minh RLS bằng token `anon`, anonymous authenticated user A/B và service role: không user nào đọc Gift Code/hash/audit; user không mutate trực tiếp; Gateway service role gọi được hai RPC.
5. Cấu hình Gateway secret store: database connection string SSL, Supabase URL/publishable key, private ES256 key, `Gateway:DeviceEntitlements:Enabled=true`, `GrantLifetimeMinutes` trong 5–1440. Không đặt private key/service-role secret trong client hoặc repository.
6. Cấu hình client public: Supabase URL, publishable key, Gateway HTTPS URL, public ES256 key.
7. Smoke test thật: first-run anonymous identity → stable Device Code sau restart → redeem duration/credit → replay/race bị chặn → offline dùng đến grant expiry → blocked/revoked fail-closed → gói hết hạn vẫn xem được account/dự án cục bộ.

## Trạng thái xác minh

| Hạng mục | Trạng thái |
|---|---|
| Local schema/code/build | PASS |
| Local capability/grant tests | PASS |
| Hosted schema/migration | NOT VERIFIED |
| Hosted RLS | NOT VERIFIED |
| Anonymous Auth hosted | NOT CONFIGURED/NOT VERIFIED |
| Hosted end-to-end Gift Code | NOT VERIFIED |
| Push `develop` | NOT AVAILABLE — repository không có remote |

