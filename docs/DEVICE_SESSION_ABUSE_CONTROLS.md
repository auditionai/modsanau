# Kiểm soát thiết bị và phiên — PLAN 86

## Phạm vi và trạng thái

PLAN 86 bổ sung một lớp authorization server-side tùy chọn cho các endpoint cloud/thương mại. Đây là kiểm soát lạm dụng
tài khoản, không phải DRM, không chứng minh quyền sở hữu máy và không can thiệp project/edit/build/export local.

Trạng thái: `IMPLEMENTED CONTRACT / LOCAL POSTGRESQL VERIFIED / PRODUCTION NOT VERIFIED`.

## Danh tính và dữ liệu tối thiểu

- Supabase bearer token vẫn là authentication authority. Gateway lấy `user_id` duy nhất từ principal đã xác minh online;
  request đăng ký không có trường `UserId`.
- Desktop tạo `DeviceId` UUID ngẫu nhiên, không đọc MachineGuid, CPU, disk, MAC, registry, IP hay game installation.
- Gateway/PostgreSQL tạo `SessionId` UUID. Hai UUID là identifier, không phải secret hoặc credential thay bearer token.
- Windows Credential Manager giữ binding `DeviceId`/`SessionId` riêng với Supabase access/refresh token. Không có token mới
  trong file/settings/plaintext.
- Bảng chỉ giữ user/device/session UUID, nhãn chung, client version, platform, `created_at`, `last_seen_at`, `revoked_at`.
  Không giữ IP, hardware fingerprint, bearer/refresh token, prompt, ảnh hoặc payment data.

## Luồng và API

Khi cả desktop và Gateway cùng bật feature, desktop xác minh đăng ký một lần mỗi process trước cloud request đầu tiên:

1. tải hoặc tạo `DeviceId` ngẫu nhiên trong Credential Manager;
2. gửi bearer tới `POST /v1/device-sessions/register` cùng metadata có giới hạn;
3. lưu binding server-returned và thêm `X-Audition-Device-Id` + `X-Audition-Session-Id` vào request cloud;
4. Gateway xác minh exact `(verified user, device, session, not revoked)` trước endpoint thương mại.

Endpoint quản lý đều cần bearer và named per-user rate limit hiện hữu:

- `POST /v1/device-sessions/register`: idempotent cho cùng thiết bị đang hoạt động;
- `GET /v1/device-sessions`: chỉ liệt kê phiên của verified user;
- `DELETE /v1/device-sessions/{sessionId}`: thu hồi own session, replay thu hồi là idempotent.

Các endpoint quản lý được miễn chính session gate để user vẫn có đường đăng ký/xem/thu hồi, nhưng không được miễn bearer,
HTTPS, pre-auth IP limit, request bounds hoặc per-user rate limit. Health và Stripe webhook giữ boundary riêng.

## Cấu hình và rollout

Gateway mặc định tắt:

```text
Gateway__DeviceSessions__Enabled=true
Gateway__DeviceSessions__MaximumActiveDevices=<1..100, quyết định của server/product>
Gateway__DeviceSessions__LastSeenWriteIntervalSeconds=<60..86400>
```

Desktop opt-in bằng `AUDITION_DEVICE_SESSIONS_ENABLED=true`. Phải rollout Gateway trước rồi mới bật client. Gateway bật nhưng
thiếu/sai maximum sẽ fail closed; không có số giới hạn tùy ý hard-code. Quá giới hạn trả `DEVICE_LIMIT_REACHED`, không tự revoke
thiết bị cũ. `last_seen_at` chỉ update sau interval cấu hình để tránh ghi trên mọi request.

## Thu hồi và khả dụng local

Thu hồi phiên hiện tại làm AI/premium/credit cloud request kế tiếp trả `DEVICE_SESSION_INACTIVE`; không chờ access-token `exp`.
Nó không xóa Supabase credential, wallet, entitlement, payment ledger, project, ảnh, template, working copy hay archive. Các dịch
vụ local không phụ thuộc session middleware/database và tiếp tục edit/build/export/delete project bình thường.

Một `DeviceId` đã revoke không được đăng ký lại cho cùng user. Khôi phục tài khoản/thiết bị mất cần product support flow được
phê duyệt riêng; PLAN 86 không thêm admin bypass, sign-out-other hay silent eviction.

## Database và quyền

`private.user_device_sessions` dùng `ENABLE` + `FORCE ROW LEVEL SECURITY`; `authenticated` chỉ được SELECT các cột an toàn
qua own-row policy `(SELECT auth.uid()) = user_id`. Không client role nào có INSERT/UPDATE/DELETE hoặc EXECUTE ba mutation RPC.
`service_role` chỉ có SELECT table và EXECUTE exact register/validate/revoke function; function là `SECURITY DEFINER` với
fixed `search_path`. Advisory transaction lock theo user serialize kiểm tra giới hạn thiết bị.

## Evidence và giới hạn còn lại

Gate PostgreSQL 17.6 cô lập đã chứng minh idempotency, concurrent max-device, cross-user denial, revoke/replay, immediate deny,
last-seen throttling, RLS/column/DML/RPC và credit wallet không bị xóa. HTTP tests chứng minh forged `UserId` bị schema reject,
AI bị chặn trước trusted service và management API vẫn truy cập được sau current-session revoke.

Chưa xác minh Supabase staging/live, edge/distributed rate limit, alert/SIEM, support recovery hay cấu hình production. UUID client
không chống được client đã patch hoặc attacker có bearer; nó chỉ cung cấp server-bound session, quota và revoke point. Tín hiệu
“thiết bị mới” hiện là durable registration + `IsNewDevice`, chưa phải risk engine và không dùng invasive fingerprint.
