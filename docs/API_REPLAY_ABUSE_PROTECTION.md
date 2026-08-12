# Bảo vệ API trước replay và lạm dụng

## Phạm vi

PLAN 82 bảo vệ trust boundary `AuditionModStudio.Gateway`; không chuyển quyền auth, credit, entitlement hoặc payment
sang desktop. Control áp dụng theo thứ tự:

1. Chỉ chấp nhận HTTPS; request cleartext bị trả `HTTPS_REQUIRED`, không redirect bearer token hoặc body.
2. Giới hạn theo IP trước authentication để giảm flood vào Supabase validation.
3. Xác minh bearer token online tại exact Supabase `/auth/v1/user` HTTPS endpoint, redirects bị tắt.
4. JWT phải có exact `sub`, `iat`, `exp`; lifetime tối đa một giờ, clock skew tối đa năm phút và `nbf` tương lai bị
   reject. Claims chỉ dùng để thu hẹp acceptance; online Supabase validation vẫn là authority và returned user phải
   trùng `sub`.
5. Endpoint authenticated bị fixed-window limit theo verified user; `/v1/ai/jobs` dùng ngưỡng charged-operation riêng.
   Queue luôn bằng 0.
6. JSON POST chỉ nhận `application/json`; upload chỉ nhận image media type allowlist hiện hữu. Endpoint size metadata
   được kiểm tra trước binding khi có `Content-Length`; Kestrel limit vẫn bảo vệ streaming/chunked body.
7. Service/SQL luôn nhận user từ verified principal. AI job lookup/content access đều bind owner; client không gửi
   `UserId`, credit cost hoặc provider authority.
8. Charged AI enqueue tiếp tục bắt buộc idempotency key. PostgreSQL canonical request hash trả deterministic replay,
   reject cùng key/khác payload và không double-reserve. Signed entitlement grant tiếp tục dùng one-time nonce.
9. Mỗi request sinh structured audit event với method, route template, status, subject fingerprint và duration. Không
   log Authorization, raw UUID, query, body, prompt, image, provider secret hoặc DB connection string.

Request timestamp/nonce mới không được thêm vào AI enqueue vì durable idempotency là retry/replay semantic phù hợp:
replay cùng payload phải nhận lại kết quả, không bị biến thành lỗi. Timestamp nằm trong short-lived access token; nonce
one-time chỉ dùng ở signed entitlement grant, nơi replay phải bị từ chối.

## Cấu hình

Các key dưới `Gateway:AbuseProtection` có secure default:

| Key | Mặc định | Giới hạn code |
|---|---:|---:|
| `RequireHttps` | `true` | boolean |
| `PreAuthenticationPermitLimit` | `120` | `1..10000` |
| `AuthenticatedPermitLimit` | `60` | `1..10000` |
| `ChargedOperationPermitLimit` | `10` | `1..1000` |
| `RateLimitWindowSeconds` | `60` | `1..3600` |
| `MaximumAccessTokenLifetimeSeconds` | `3600` | `300..3600` |
| `AccessTokenClockSkewSeconds` | `120` | `0..300` |
| `TrustedProxies` | rỗng | tối đa 8 exact IP |

Khi TLS terminate ở reverse proxy, chỉ khai báo exact proxy IP trong `TrustedProxies`. Forward limit là một hop;
`X-Forwarded-For` và `X-Forwarded-Proto` từ nguồn không tin cậy không được chấp nhận. Không clear known-proxy list để
tin mọi nguồn. Nếu không có trusted proxy, Kestrel phải terminate TLS trực tiếp.

## Vận hành và residual risk

- Limiter trong repository là per-process. Production scale-out cần distributed edge/WAF limiter và metric/alert; không
  được mô tả test một instance là distributed protection.
- Structured log cần chuyển tới append-restricted centralized sink với retention/access policy. Repository chưa chứng
  minh SIEM, alert, immutable audit retention hoặc production incident response.
- Supabase project production phải cấu hình JWT expiry không quá một giờ. Gateway enforcement đã test, nhưng dashboard
  project/live token chưa được xác minh.
- TLS certificate, termination, trusted proxy topology, stress threshold và audit sink phải có deployment evidence.
- Rate limiting giảm abuse, không phải quota/billing authority. Credit transaction và idempotency SQL vẫn là boundary.
- Không có certificate pinning; platform certificate validation và rotation-friendly HTTPS được giữ nguyên.

Trạng thái: `IMPLEMENTED CONTRACT / PRODUCTION DEPLOYMENT NOT VERIFIED`.
