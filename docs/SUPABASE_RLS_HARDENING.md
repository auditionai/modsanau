# Supabase RLS hardening

## Inventory và access matrix

PLAN 83 audit toàn bộ bảng có `user_id` sau migrations PLAN 60/62/65 trong cùng schema `private`; không tạo wallet
schema hoặc credit service thứ hai.

| Bảng | Authenticated own-row read | Cột bị giữ server-only | Client mutation |
|---|---|---|---|
| `credit_wallets` | balance/version/timestamps | không | denied |
| `credit_reservations` | identity/amount/status/timestamps | transaction lineage | denied |
| `credit_ledger` | transaction/amount/delta/balance lineage | `authority_reference` | denied |
| `credit_refunds` | own refund lineage/amount/timestamp | không | denied |
| `credit_idempotency` | denied | toàn bảng replay-control | denied |
| `ai_jobs` | contract-safe job/history columns | reservation/capture IDs, provider request ID, idempotency/hash, lease | denied |

Mỗi bảng bật cả `ENABLE ROW LEVEL SECURITY` và `FORCE ROW LEVEL SECURITY`. Năm bảng có SELECT policy exact
`TO authenticated USING ((SELECT auth.uid()) = user_id)`; `credit_idempotency` cố ý không có client policy/grant.
Column-level SELECT grant thu hẹp dữ liệu ngay cả khi row thuộc user. `anon` không có schema usage.

Authenticated không có INSERT/UPDATE/DELETE hoặc EXECUTE private function. Credit/job mutation tiếp tục chỉ qua exact
`SECURITY DEFINER` functions đã grant cho backend `service_role`; Gateway vẫn lấy user từ verified bearer principal.
Service-role key bypass RLS theo mô hình Supabase và vì vậy tuyệt đối không được đưa vào App/repository/log.

## Migration và test gate

Migration: `supabase/migrations/202608120004_plan83_supabase_rls_hardening.sql`.

Automated contract tests kiểm tra đủ 6 bảng, policy count/shape, FORCE RLS, column denylist, không client DML/RPC và future
function default privilege. PostgreSQL integration test tạo database ngẫu nhiên trên loopback, apply tuần tự PLAN 60/62/65/83,
seed bằng trusted admin role, rồi chứng minh:

- owner đọc được permitted rows;
- user khác nhận 0 row trên cả năm readable tables;
- internal table/columns trả insufficient privilege;
- authenticated UPDATE/DELETE/credit RPC bị từ chối;
- anon không đọc được schema;
- catalog có đúng SELECT-only policies, không mutation policy.

Gate local dùng official PostgreSQL `17.6-alpine3.22` tại immutable image digest
`sha256:ef257d85f76e48da1c64832459b59fcaba1a4dac97bf5d7450c77753542eee94`. Container bind random port chỉ trên
`127.0.0.1`, dùng password ngẫu nhiên trong process, database tên ngẫu nhiên và được drop/stop sau test.

## Production status và residual risk

- Local real-PostgreSQL RLS/privilege evidence là VERIFIED; Supabase staging/live Data API, exposed-schema config, role
  ownership và migration backup/rollback vẫn `PRODUCTION NOT VERIFIED`.
- Column-level grants yêu cầu client query explicit column list; wildcard `SELECT *` bị từ chối có chủ ý.
- Superuser, table owner khi không FORCE, và role có `BYPASSRLS` là privileged boundaries. FORCE đã bật cho owner;
  `service_role` vẫn bypass theo thiết kế và phải nằm trong server secret store.
- Foreign-key/uniqueness enforcement có thể tạo covert-channel nhỏ theo PostgreSQL semantics; client không có DML nên exposure
  bị giảm đáng kể.
- Real gate phát hiện PLAN 60 functions hiện lỗi `42702` do output parameter trùng tên cột. PLAN 83 seed qua trusted admin
  để cô lập RLS evidence; lỗi ledger không bị sửa tại đây và là input bắt buộc cho exact PLAN 85 concurrency gate.

Trạng thái: `RLS CONTRACT + LOCAL POSTGRES VERIFIED / SUPABASE PRODUCTION NOT VERIFIED`.
