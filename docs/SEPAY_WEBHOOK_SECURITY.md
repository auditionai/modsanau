# Bảo mật webhook SePay

Đối chiếu tài liệu SePay ngày 15/08/2026. Production chọn HMAC-SHA256, không dùng `No_Authen`.

## Contract xác thực

- Header `X-SePay-Timestamp` là Unix seconds.
- Header `X-SePay-Signature` có dạng `sha256=<64 hex>`.
- Chuỗi ký là raw bytes của `<timestamp>.<raw_body>`.
- Timestamp chỉ hợp lệ trong cửa sổ +/- 300 giây.
- So sánh signature theo constant-time byte comparison.
- Body tối đa 64 KiB, bắt buộc JSON và chỉ parse sau khi HMAC hợp lệ.

Webhook hợp lệ chỉ trả `200 {"success":true}` sau khi RPC đã persist/fulfill hoặc xác nhận duplicate idempotent. Invalid auth trả 401; malformed payload trả 400; cấu hình/DB transient trả 5xx để provider retry. SePay chấp nhận 200/201, response timeout 30 giây và retry Fibonacci tối đa 7 lần/5 giờ theo tài liệu đã đọc.

## Validation

Payload normalize chỉ giữ provider transaction ID, reference, gateway, transaction date, transfer direction, integer amount, payment code/content có giới hạn và merchant account number. Không lưu tên khách hàng hoặc bank payload thừa. Endpoint không tải URL, không gọi callback, không nối chuỗi SQL và không log raw body/secret/account token.

## Replay và concurrency

Provider transaction ID là dedupe authority. Same-event retries được serialize bằng advisory lock rồi trả kết quả replay. Unique provider transaction, unique matched order, unique fulfillment event, Credit Ledger reference và subscription fulfillment reference bảo vệ nhiều lớp. Event khác cùng order không thể fulfillment lần hai hoặc hạ trạng thái order đã hoàn tất.

## Vận hành

- Rotate HMAC secret bằng secret store và cập nhật SePay trong maintenance window có giám sát.
- Alert theo tỷ lệ 401, 5xx, `review_required`, duplicate conflict và latency.
- Không ghi signature, secret, API token hoặc raw bank content vào log/screenshot.
- `SEPAY_API_TOKEN` là high privilege, backend-only và chỉ dùng cho bounded reconciliation.
- Cần kiểm chứng live webhook URL, account filter, incoming-only filter và clock synchronization trước production.
