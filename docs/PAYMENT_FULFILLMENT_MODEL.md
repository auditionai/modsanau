# Mô hình fulfillment thanh toán

## State machine

```text
waiting_payment -> paid -> fulfilling -> fulfilled
       |             |         |
       +-> expired   +---------+-> retry idempotent
       +-> cancelled
       +-> review_required

review_required: wrong account, underpayment, overpayment, late/cancelled payment
refunded: trạng thái ghi nhận tương lai, không có refund automation trong PLAN 107
```

Chỉ incoming transaction, đúng merchant account, đúng order code, đúng integer amount và order còn `waiting_payment` mới được tự động fulfillment. Unknown code/outgoing được ghi nhận ở trạng thái rejected nhưng không match order. Giao dịch thứ hai cùng nhắc một order được ghi `review_required` dưới dạng candidate và không được thay đổi order đã fulfillment.

## Invariant cơ sở dữ liệu

- Một provider transaction chỉ tồn tại một lần.
- Một provider transaction chỉ match tối đa một order; một order chỉ có tối đa một authoritative matched transaction.
- Một order có tối đa một fulfillment event và một provider transaction ID.
- Snapshot/amount/expiry/order identity bất biến sau khi tạo.
- Fulfillment event và payment audit append-only.
- Order creation idempotent theo device profile và key.

Advisory lock theo provider transaction chặn concurrent duplicate trước unique insert. Row lock trên order serialize hai event khác nhau cùng order. Unique constraints vẫn là lớp bảo vệ cuối cùng.

## Subscription

Trong transaction đã lock:

```text
base = greatest(current subscription expiry, server now)
new expiry = base + purchased duration
```

Subscription hết hạn bắt đầu từ server time; subscription còn active không mất ngày. Fulfillment event dùng unique authority `SEPAY_ORDER:<order_id>`.

## Credits

Credits gọi `private.credit_grant` với authority `SEPAY_ORDER:<order_id>`. Không update wallet trực tiếp. Credit Ledger và credit idempotency giữ append-only reference/transaction, vì vậy retry không cộng lần hai.

## Crash và reconciliation

Match, chuyển `paid`, mutation commercial authority, fulfillment event và chuyển `fulfilled` chạy trong cùng PostgreSQL transaction. Lỗi giữa các bước rollback toàn bộ để SePay retry an toàn. `reconcile`/Admin retry chỉ nhận order `paid` hoặc `fulfilling`; owner + MFA + recent login + reason mới được gọi. Worker/API reconciliation tương lai phải bounded, không poll SePay liên tục và phải tôn trọng giới hạn 3 request/giây cùng retry-after.
