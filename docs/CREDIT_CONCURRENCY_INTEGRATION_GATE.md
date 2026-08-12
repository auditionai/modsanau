# Credit Concurrency Integration Gate — PLAN 85

## Kết quả

PLAN 85 chạy toàn bộ migration theo thứ tự trên PostgreSQL 17.6 thật và chứng minh trực tiếp PLAN 60 ledger qua
`PostgresCreditLedgerService`. Lỗi PostgreSQL `42702` được sửa tại chỗ bằng migration
`202608120006_plan85_credit_concurrency_fix.sql`; không tạo wallet, ledger, schema hay credit service thứ hai.

Nguyên nhân là tên output `available_credits`, `reserved_credits`, `reservation_id`, `transaction_id` của
`RETURNS TABLE` đồng thời là biến PL/pgSQL, trong khi PLAN 60 dùng một số cột không qualify trong `UPDATE/WHERE/RETURNING`.
Migration recreate đúng năm function `grant/reserve/capture/release/refund` và qualify projection bằng alias `w`/`r`;
signature, state machine, request hash, locks, privilege và caller hiện có được giữ nguyên.

## Real PostgreSQL evidence

`CreditConcurrencyIntegrationTests` tạo database random riêng cho mỗi scenario, apply mọi migration, dùng pooled
connections riêng và đồng bộ thời điểm bắt đầu các calls song song. Gate kiểm:

- lifecycle thật `grant 100 → reserve 60 → capture 40 → refund 15`, balance cuối `75/0` và ledger lineage đúng;
- tám reserve 30 khác key trên wallet 100: đúng ba success, năm `CREDIT_INSUFFICIENT`, balance `10/90`;
- tám request cùng key/payload: đúng một `CREDIT_APPLIED`, bảy replay, cùng reservation/transaction và một ledger row;
- cùng key nhưng amount 20/30: đúng một apply, một `CREDIT_IDEMPOTENCY_CONFLICT`, không partial mutation;
- capture 40 chạy song song release trên reserve 60: đúng một terminal state/ledger transaction, loser nhận
  `CREDIT_RESERVATION_NOT_OPEN`;
- hai refund 40 khác key trên capture 60: chỉ một success, tổng refund 40, request còn lại bị over-refund rejection;
- PLAN 84 verified payment fulfillment gọi cùng repaired `credit_grant`, retry tạo một payment event và một grant row.

Mỗi rejection được kiểm lại trực tiếp bằng wallet/reservation/ledger/refund/idempotency cardinality, không chỉ dựa trên
status trả về. Test RLS PLAN 83 chạy cùng collection tuần tự để tránh test infrastructure tranh chấp global PostgreSQL roles.

## Lock và transaction invariant

- Advisory lock serialize đúng `(user, operation, idempotency key)` cho deterministic replay/conflict.
- Wallet row lock serialize các reserve khác key và giữ `available/reserved >= 0`.
- Reservation row lock quyết định duy nhất `open → captured|released`.
- Captured-ledger row lock serialize aggregate refund và ngăn tổng refund vượt capture.
- Function rejection rollback toàn call; append-only ledger/refund/idempotency không có row mồ côi.

## Môi trường và giới hạn tuyên bố

Gate dùng official PostgreSQL `17.6-alpine3.22` pin digest
`sha256:ef257d85f76e48da1c64832459b59fcaba1a4dac97bf5d7450c77753542eee94`, random loopback port, random process-only
password và fresh database; database/container được cleanup sau test.

Trạng thái: `LOCAL REAL POSTGRESQL TRANSACTION/CONCURRENCY VERIFIED / SUPABASE STAGING-LIVE NOT VERIFIED`.
Gate không chứng minh multi-region latency/failover, pool exhaustion, backup/restore, production monitoring hay live Supabase
role/topology. Client vẫn không có mutation route hoặc amount authority; pricing/payment authorities vẫn ở server.
