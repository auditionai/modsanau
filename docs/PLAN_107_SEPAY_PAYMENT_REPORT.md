# PLAN 107 - SePay Automatic Payment Integration

Status: PARTIAL

Starting HEAD: `e1c796d`

Branch: `develop`

## Kết quả triển khai

- Catalog versioned, order snapshot immutable, order code random và order idempotency.
- Edge Function `payments` cho Desktop/Landing/Admin và dedicated `sepay-webhook` HMAC raw body.
- Exact-amount matching, wrong account/late/cancelled/under/overpayment về `review_required`.
- Fulfillment subscription/Credits exactly-once trong PostgreSQL transaction.
- Desktop Account, landing pricing/purchase và Admin Payments dùng cùng server catalog.
- Admin metrics là operational view; product mutation/retry có role, MFA, recent login, reason và audit.

## Phân loại

| Hạng mục | Trạng thái | Bằng chứng |
|---|---|---|
| Payment architecture | VERIFIED cục bộ | Code review, build và contract tests |
| Hosted order backend | NOT VERIFIED | Chưa apply migration/deploy function lên hosted project |
| Webhook security | VERIFIED cục bộ | HMAC valid/invalid/stale, malformed payload, body/CSP contract |
| Subscription fulfillment | VERIFIED theo contract | DB invariant/static contract; local PostgreSQL runtime chưa chạy |
| Credit fulfillment | VERIFIED theo contract | Gọi append-only `private.credit_grant`; runtime hosted chưa chạy |
| Admin payment management | VERIFIED cục bộ | Static/admin tests; hosted chưa deploy |
| SePay API connectivity | NOT VERIFIED | Không có credential; V1 không poll API |
| SePay real webhook | NOT VERIFIED | Chưa cấu hình SePay account/webhook |
| Real-money E2E | NOT VERIFIED | Không có phê duyệt real-money test |

## Remote configuration

SePay credentials: NOT CONFIGURED

Webhook: NOT VERIFIED

Merchant bank target: NOT CONFIGURED

Supabase migrations/functions/RLS hosted: NOT VERIFIED

Netlify develop deployment: NOT CHANGED

## Test và giới hạn

Automated suite bao phủ HMAC, normalize payload, outgoing/malformed cases, 50.000 order-code sample, schema invariants, UI/CSP/backoff, Desktop purchase projection, public/admin contracts và regression suite hiện hữu. Docker Desktop không hoạt động nên migration chưa được execute trên PostgreSQL local; Supabase CLI không có linked project. In-memory Edge rate limit chưa thay thế distributed WAF/rate limit. API reconciliation worker, refund automation và accounting ledger nằm ngoài phạm vi.

Kết quả gate cục bộ:

- `dotnet build AuditionModStudio.sln --no-restore`: PASS, 0 warning/0 error.
- Payment Node tests: 5/5 PASS; Account ViewModel focused tests: 5/5 PASS.
- Public `npm run check`, Admin `npm run check`, npm audit: PASS, 0 vulnerability.
- NuGet vulnerability audit: PASS; SBOM verify: PASS, 69 package version.
- Secret scan phạm vi PLAN 107: PASS, 37 file. Scan toàn repository còn một false-positive baseline ở `LoginPage.xaml.cs`, không thuộc PLAN 107.
- `dotnet format` trên file C# PLAN 107: PASS. Full-solution format còn finding baseline ở file PLAN 104-106 không thuộc thay đổi này.
- Full solution test: các suite Core/DDS/Projects/Archives/Gateway/Imaging/Security PASS; IntegrationTests báo 13 dynamic-skip thành FAIL do thiếu approved `texconv.exe`/private fixture. Không acceptance criteria nào bị nới.
- Deno type-check và PostgreSQL runtime migration: NOT VERIFIED vì máy không có `deno` và Docker daemon không chạy.

## Screenshots

Ảnh trong `docs/evidence/plan-107/` được chụp từ production HTML/CSS/JS chạy với deterministic local API fixture, không phải mockup và không phải live SePay:

- Landing subscription/Credits pricing, confirmation, VietQR/instructions, waiting.
- Subscription success/renewed; Credits purchase/updated; expired; review-required.
- Mobile payment surface.
- Admin Payments, revenue summary và payment detail.

Desktop payment surface được build/test bằng XAML và ViewModel nhưng chưa có screenshot trung thực vì hosted catalog/order backend chưa deploy. Không thay bằng ảnh giả.

Không thể gọi production payment ready cho đến khi migration/functions được deploy, RLS và concurrent/partial-failure matrix chạy trên PostgreSQL, merchant configuration được xác nhận, webhook HMAC thật được nhận và Product Owner phê duyệt riêng real-money E2E.

Next: PLAN 108 - Commercial End-to-End Integration. Không triển khai trong PLAN 107.
