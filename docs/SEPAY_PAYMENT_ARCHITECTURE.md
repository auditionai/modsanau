# Kiến trúc thanh toán SePay

Tài liệu được đối chiếu với tài liệu chính thức SePay ngày 15/08/2026.

## Luồng tin cậy

```text
Desktop / Landing
  -> Supabase Edge Function payments
  -> public.payment_api (service role)
  -> private payment tables

Ngân hàng -> SePay -> Edge Function sepay-webhook
  -> HMAC raw body -> normalize/dedupe/match
  -> paid + fulfillment trong một PostgreSQL transaction
  -> subscription hoặc append-only Credit Ledger
```

Client chỉ nhận catalog công khai, snapshot order và thông tin chuyển khoản cần thiết. Desktop, landing và Admin không có SePay API token, webhook secret hay service-role key. `AuditionModStudio.Core` chỉ chứa `IPaymentService` và model provider-neutral; implementation Supabase nằm trong `AuditionModStudio.Cloud`.

## Product và order

- Catalog được version theo `(product_id, version)`; repository không seed giá thương mại.
- Chỉ một version hiện hành có `effective_to IS NULL`.
- Order snapshot giữ nguyên product, loại, tên, giá VND nguyên, số ngày hoặc Credits.
- Order code do server sinh: `AMS` cộng 16 ký tự hex ngẫu nhiên, không tuần tự và có unique constraint.
- Idempotency order là unique `(device_profile_id, idempotency_key)`.
- TTL lấy từ `PAYMENT_ORDER_TTL_MINUTES`, giới hạn 5-1.440 phút.
- Merchant bank/account/holder chỉ lấy từ cấu hình server. QR dùng `https://vietqr.app/img` với recipient, amount và content do server tạo.

## API

`payments` cung cấp catalog, tạo/xem/hủy order và Admin operations. Desktop dùng Device Identity session. Landing có thể nhập public Device Code; backend chỉ dùng code để resolve một `device_profile_id` đang active và không trả subscription, balance hoặc thông tin riêng của thiết bị. Lookup có phản hồi lỗi chung và rate limit; rate limiter Edge V1 là per-instance nên production vẫn cần rate limit phân tán/WAF.

`sepay-webhook` là endpoint backend-only, không CORS và không callback tới URI trong payload. URL production ổn định dự kiến:

```text
https://plvuutsjwsawkkrmvigz.supabase.co/functions/v1/sepay-webhook
```

## Cấu hình triển khai

Các secret/config phải đặt bằng Supabase secret store, không đặt trong client/repository:

- `SEPAY_WEBHOOK_SECRET`: HMAC secret, tối thiểu 32 ký tự.
- `SEPAY_API_TOKEN`: chỉ dành cho reconciliation API khi triển khai; code V1 không gọi API liên tục.
- `PAYMENT_BANK_ACCOUNT`, `PAYMENT_BANK_CODE`, `PAYMENT_ACCOUNT_HOLDER`.
- `PAYMENT_ORDER_TTL_MINUTES`.
- `PAYMENT_ALLOWED_ORIGINS`: allowlist origin, phân tách bằng dấu phẩy.

Development, staging và production phải dùng webhook/secret/bank configuration tách biệt. Không trỏ financial webhook production vào branch preview.

## Admin và giới hạn

Admin có metrics vận hành, order detail, product versioning và retry order `paid`/`fulfilling`. Mutation tài chính yêu cầu role phù hợp, MFA verified, đăng nhập trong 15 phút gần nhất, lý do tối thiểu và correlation ID audit. Revenue là tổng order đã fulfillment, không phải sổ kế toán. Refund/chargeback automation nằm ngoài PLAN 107.
