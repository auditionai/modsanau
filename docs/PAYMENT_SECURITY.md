# Bảo mật thanh toán — PLAN 84

## Kết quả và ranh giới

Gateway có một ingress thanh toán duy nhất: `POST /v1/payments/webhooks/stripe`. Endpoint không dùng bearer token vì
Stripe là caller, nhưng không anonymous về mặt trust: chỉ raw body có `Stripe-Signature` HMAC-SHA256 hợp lệ trong cửa sổ
5 phút mới đi tới fulfillment. Client desktop, browser redirect và màn hình “payment success” không có route/contract cấp
credit và không phải bằng chứng thanh toán.

PLAN 84 triển khai concrete Stripe Checkout webhook, chưa tạo payment-provider abstraction; abstraction thuộc PLAN 94.
Không có checkout UI, subscription, dispute/refund provider hay thay đổi game/runtime trong phạm vi này.

## Luồng authority

1. Gateway bắt buộc HTTPS, `application/json`, giới hạn 128 KiB và per-IP pre-auth rate limit của PLAN 82.
2. Endpoint giữ nguyên byte body, nhận đúng một `Stripe-Signature`, parse bounded `t`/`v1`, tính HMAC-SHA256 trên
   `timestamp.raw_body`, so sánh constant-time và reject timestamp lệch quá 5 phút.
3. Chỉ `checkout.session.completed` hoặc `checkout.session.async_payment_succeeded`, `mode=payment`,
   `payment_status=paid` và `livemode` đúng cấu hình được xét. Event khác hoặc Checkout chưa paid được acknowledge nhưng
   không fulfill; async success hợp lệ sau đó vẫn được xử lý.
4. `client_reference_id` phải là UUID khác rỗng. `metadata.credit_product_id` chỉ là selector; server catalog bind exact
   product với `amount_total`, currency và số credit. Payload/provider không tự chọn số credit.
5. Gateway tạo SHA-256 của exact payload và canonical grant request, rồi gọi duy nhất
   `private.payment_apply_verified` qua server database connection.
6. PostgreSQL advisory-lock theo provider/payment identity. Unique `(provider,event)` và `(provider,payment)` cùng stored
   hashes tạo idempotency bền vững; replay đúng trả success cũ, conflict payload fail closed.
7. Chỉ sau các gate trên, function gọi đúng PLAN 60 `private.credit_grant`. `payment_events` và credit ledger là append-only.

## Cấu hình server-only

- `Gateway:Payments:Stripe:WebhookSecret`: endpoint signing secret; không thuộc client/repository/log.
- `Gateway:Payments:Stripe:LiveMode`: phải khớp exact `livemode` của event.
- `Gateway:Payments:Products:*`: `ProductId`, `AmountMinor`, currency ba chữ thường và `Credits` nguyên dương.
- `Gateway:CreditDatabaseConnectionString`: kết nối TLS server-only hiện có.

Thiếu/sai secret, catalog hoặc database làm verifier/fulfillment trả unavailable; không có fallback cấp credit. Object cấu
hình payment và database luôn render `[REDACTED]`. Audit chỉ ghi method, route template, status, subject fingerprint và
duration; không ghi header chữ ký, raw body, payment/customer identity hay secret.

## Database privilege

Migration `202608120005_plan84_payment_security.sql` bật ENABLE+FORCE RLS cho `private.payment_events`, không tạo client
policy, revoke `PUBLIC`/`anon`/`authenticated` và chỉ cấp `service_role` SELECT cùng EXECUTE exact fulfillment function.
Function là `SECURITY DEFINER` với fixed `search_path`; input có grammar/length/range checks trước lock/grant.

## Evidence và giới hạn tuyên bố

`PaymentSecurityTests` kiểm HMAC trên exact raw bytes, tamper/secret/timestamp, live/test mismatch, product/amount/currency,
event ignore, fail-closed configuration, anonymous HTTP ingress, size/type gate, không có success authority route và SQL
idempotency/privilege contract.

Thiết kế bám theo tài liệu Stripe về [xác minh chữ ký trên raw body](https://docs.stripe.com/webhooks/signature),
[retry, duplicate event và timestamp tolerance](https://docs.stripe.com/webhooks), cùng
[Checkout event types](https://docs.stripe.com/api/events/types).

Chưa có Stripe live endpoint/secret/product/observability evidence; trạng thái là
`IMPLEMENTED CONTRACT / PRODUCTION PAYMENT NOT VERIFIED`. Real PostgreSQL gate PLAN 83 đã phát hiện ambiguity `42702` trong
function PLAN 60; PLAN 85 kế tiếp phải sửa và chạy payment/ledger transaction thực trước khi fulfillment database được coi
là vận hành được. Không đổi test giả/offline thành production evidence.
