# Commercial E2E Architecture — Plan 108

## System Overview

Audition AI Mod Studio commercial stack kết nối:

```
Desktop App (WinUI 3)
    ↓
Device Identity Service (Supabase RPC)
    ↓
Entitlement Service (Supabase RPC + Signed Grant)
    ↓
Payment Service (Supabase Edge Function + SePay Webhook)
    ↓
Credits Service (Supabase RPC + Ledger)
    ↓
AI Studio Service (Supabase Edge Function → Trạm Sáng Tạo)
    ↓
Admin Portal (Netlify + Supabase RLS)
    ↓
Audit Trail (Append-only tables)
```

---

## Component Responsibilities

### Desktop App

**Technology:** .NET 8, WinUI 3, C#

**Responsibilities:**
- Generate/store device keypair
- Register device identity
- Cache signed entitlement grant
- Display subscription/credits/entitlement state
- Create payment orders
- Poll payment status
- Redeem giftcodes
- Submit AI jobs with entitlement check
- Poll AI job status
- Download/preview AI results
- Apply AI results to local project
- Portable file-only architecture

**Authority:**
- NONE for commercial grants
- Local project editing only
- Cached entitlement grant until expiry

**Security:**
- Stores device credential securely (encrypted file or credential manager)
- Never exposes private key
- Validates signed entitlement grant signature
- Uses HTTPS only
- No hardcoded secrets

---

### Device Identity Service

**Technology:** PostgreSQL + Supabase RPC

**Function:** `public.device_identity_api(action, payload)`

**Actions:**
- `register`: Create new device profile
- `refresh`: Load device profile + subscription + wallet

**Authority:**
- Issues device_profile_id
- Issues public_device_code
- Validates device_fingerprint uniqueness
- Returns signed entitlement grant

**Schema:**
- `private.device_profiles`
- `private.device_keypairs`
- `private.subscriptions`
- `private.credit_wallet`

**RLS:** Service role only (desktop authenticates via user JWT, RPC validates device ownership)

---

### Entitlement Service

**Technology:** PostgreSQL function

**Function:** `private.device_entitlement_grant_build(device_profile_id)`

**Authority:**
- Computes capabilities based on subscription + device status
- Signs grant with server secret
- Sets grant expiry (typically 15 minutes)
- Returns `DeviceEntitlementSnapshot` with `SignedGrant`

**Verification:**
- Desktop validates signature before trusting capabilities
- Desktop checks `GrantExpiresAt` before using cached grant
- Refresh triggered by: payment, giftcode, admin action, expiry

**Schema:**
```json
{
  "DeviceCode": "AUBIZ-ABC123",
  "DeviceStatus": "Active",
  "SubscriptionStatus": "Active",
  "SubscriptionExpiresAt": "2026-09-15T00:00:00Z",
  "AvailableCredits": 5000,
  "Capabilities": {
    "CanUseAi": true,
    "CanBuild": true,
    "CanExport": true,
    "CanUsePremiumTemplates": true
  },
  "ObservedAt": "2026-08-15T10:30:00Z",
  "GrantExpiresAt": "2026-08-15T10:45:00Z",
  "SignedGrant": "<base64-jwt-style-signature>"
}
```

---

### Payment Service

**Technology:** Supabase Edge Function + PostgreSQL RPC + SePay Webhook

**RPC:** `public.payment_api(action, payload)`

**Actions:**
- `catalog`: List active payment products
- `create_order`: Create payment order with idempotency
- `order_status`: Get order status
- `cancel_order`: Cancel waiting order
- `ingest_sepay`: Process SePay webhook (service_role only)
- `reconcile`: Retry fulfillment (admin only)

**Flow:**
1. Desktop calls `create_order` with product_id + idempotency_key
2. Server creates `payment_orders` row with unique `order_code`
3. Server returns payment instructions (bank account, QR, amount, content)
4. Desktop displays QR + instructions
5. Desktop polls `order_status` every 4-30 seconds
6. User transfers money via bank
7. SePay webhook → Supabase Edge Function → `ingest_sepay`
8. Server matches transaction to order via `payment_code`
9. Server validates amount, account, expiry
10. Server calls `payment_fulfill_locked()`
11. Fulfillment: subscription extended OR credits granted
12. Desktop poll sees `status: fulfilled`
13. Desktop refreshes entitlement

**Authority:**
- Payment order creation: validated device + active product
- Fulfillment trigger: SePay webhook signature verified
- Fulfillment execution: transactional, idempotent via UNIQUE constraints
- Desktop cannot fulfill itself

**Schema:**
- `private.payment_products`
- `private.payment_orders`
- `private.sepay_transactions`
- `private.payment_fulfillment_events` (append-only)
- `private.payment_audit_events` (append-only)

**Idempotency:**
- Order creation: `(device_profile_id, idempotency_key)` UNIQUE
- Webhook: `provider_transaction_id` UNIQUE
- Fulfillment: `payment_order_id` UNIQUE in fulfillment_events

---

### Credits Service

**Technology:** PostgreSQL append-only ledger

**Functions:**
- `private.credit_grant(user_id, amount, reason, idempotency_key, request_hash)`
- `private.credit_reserve(user_id, amount, reason, idempotency_key, request_hash)`
- `private.credit_capture(user_id, reservation_id, amount, idempotency_key, request_hash)`
- `private.credit_release(user_id, reservation_id, idempotency_key, request_hash)`

**Authority:**
- All mutations via stored procedures
- Ledger append-only (trigger rejects UPDATE/DELETE)
- Wallet derived from ledger
- Request hash prevents duplicate grants

**Schema:**
- `private.credit_ledger` (append-only, immutable)
- `private.credit_wallet` (computed view/trigger-maintained)

**Transaction Flow:**

**Purchase Credits:**
```
Payment fulfilled → credit_grant() → ledger entry → wallet updated
```

**AI Job Success:**
```
Submit → credit_reserve() → Processing → Complete → credit_capture()
```

**AI Job Failure:**
```
Submit → credit_reserve() → Failed → credit_release()
```

---

### Giftcode Service

**Technology:** PostgreSQL RPC

**Function:** `public.gift_api(action, payload)`

**Actions:**
- `redeem`: Redeem giftcode for device

**Authority:**
- Validates code exists, not expired, not revoked
- Checks `max_redemptions` limit
- Checks device not already redeemed
- Grants duration OR credits
- Records redemption event

**Schema:**
- `private.gift_codes`
- `private.gift_redemptions` (UNIQUE per device)

**Idempotency:**
- `(gift_code_id, device_profile_id)` UNIQUE
- Retry same code → "đã được sử dụng"

---

### AI Studio Service

**Technology:** Supabase Edge Function + Trạm Sáng Tạo API + PostgreSQL RPC

**Edge Function:** `functions/tst-image`

**RPC:** `public.ai_image_api(action, payload)`

**Actions (RPC):**
- `prepare`: Create job, reserve credits, return job_id
- `submitted`: Mark job submitted to provider
- `complete`: Capture credits, mark completed
- `fail`: Release credits, mark failed
- `status`: Get job status
- `history`: List recent jobs

**Flow:**
1. Desktop checks `CanUseAi` capability
2. Desktop calls Edge Function `/tst-image?action=generate`
3. Edge Function validates auth
4. Edge Function calls RPC `ai_image_api('prepare')` → reserves credits
5. Edge Function submits to Trạm Sáng Tạo API
6. Edge Function calls RPC `ai_image_api('submitted')` with provider_job_id
7. Desktop polls Edge Function `/tst-image?action=status`
8. Edge Function polls Trạm Sáng Tạo job status
9. When complete, Edge Function calls RPC `ai_image_api('complete')` → captures credits
10. Desktop downloads result image
11. Desktop previews in AI Studio
12. User clicks "Dùng kết quả này"
13. Desktop applies to local project

**Authority:**
- Entitlement checked: `CanUseAi` + active subscription
- Credits reserved BEFORE provider submission
- Credits captured exactly once on success
- Credits released exactly once on failure
- Provider job_id UNIQUE prevents duplicate submission
- Idempotency key prevents duplicate reservation

**Schema:**
- `private.ai_jobs`
- `private.ai_provider_jobs`
- `private.credit_ledger` (reserve/capture/release entries)

**Billing:**
- Cost determined by model + settings
- Pricing from Trạm Sáng Tạo live API (cached 5 minutes)
- Quote shown before submission
- Actual cost captured after completion

---

### Admin Portal

**Technology:** React + Vite + Supabase Client SDK + Netlify

**Hosted:** https://modsanau.netlify.app/admin

**RPC:** Multiple admin-scoped actions in various RPCs

**Functions:**
- View devices, subscriptions, credits, payments, giftcodes, AI jobs
- Block/unblock device
- Extend subscription manually
- Grant/deduct credits manually
- Create/revoke giftcodes
- Retry payment fulfillment
- View audit trail
- Manage payment products

**Authority:**
- `private.admin_users` table with role + MFA state
- Sensitive actions require:
  - Role: owner or editor (not auditor)
  - MFA state: `MFA_VERIFIED`
  - Recent auth: within 5 minutes
  - Reason: min 8 characters
- RLS: admin can see all devices/orders
- Audit: every admin mutation recorded

**Schema:**
- `private.admin_users`
- `private.admin_audit` (append-only)

---

### Audit Trail

**Technology:** PostgreSQL append-only tables

**Tables:**
- `private.admin_audit`: Admin actions
- `private.payment_audit_events`: Payment lifecycle
- `private.device_commercial_events`: Device commercial events
- `private.gift_redemptions`: Giftcode redemptions (immutable)
- `private.credit_ledger`: Credit mutations (immutable)
- `private.ai_jobs`: AI job lifecycle (state machine)

**Guarantees:**
- Append-only: triggers reject UPDATE/DELETE
- Correlation ID: UNIQUE per event
- Timestamp: server time, not client
- Actor: authenticated user_id or admin_user_id
- Safe metadata: no secrets logged

---

## Data Flow Examples

### E2E: First Launch

```
1. Desktop launches
2. No device credential found
3. Desktop generates RSA keypair
4. Desktop calls device_identity_api('register')
   - Payload: { publicKey, deviceFingerprint }
5. Server creates device_profile, issues device_code
6. Server returns profile + entitlement grant
7. Desktop stores credential securely
8. Desktop displays Device Code in Account
9. Desktop shows subscription: "Chưa kích hoạt"
10. Desktop shows credits: 0
```

### E2E: Purchase Subscription

```
1. User opens Account → Gia hạn
2. Desktop calls payment_api('catalog')
3. Server returns active products
4. Desktop displays products
5. User selects "Gói 30 ngày · 50,000 đ"
6. Desktop calls payment_api('create_order')
   - Payload: { userId, productId, idempotencyKey, ttlMinutes }
7. Server creates payment_order, returns instructions
8. Desktop displays QR + bank details
9. Desktop starts polling payment_api('order_status')
10. User transfers money
11. SePay webhook → Edge Function → payment_api('ingest_sepay')
12. Server matches transaction → payment_fulfill_locked()
13. Server extends subscription (expires_at += 30 days)
14. Server records fulfillment event
15. Desktop poll sees status: fulfilled
16. Desktop calls device_identity_api('refresh')
17. Server returns updated entitlement grant
18. Desktop displays: "Đang hoạt động · Còn 30 ngày"
```

### E2E: AI Generation

```
1. User opens AI Studio
2. Desktop checks capability: CanUseAi = true
3. User enters prompt, selects model
4. Desktop calls Edge Function /tst-image?action=generate
   - Headers: Authorization: Bearer <user-jwt>
   - Body: { prompt, model, idempotency_key }
5. Edge Function validates JWT
6. Edge Function calls ai_image_api('prepare')
   - Server checks entitlement
   - Server reserves 500 credits
   - Server creates ai_jobs row
7. Edge Function submits to Trạm Sáng Tạo API
8. Trạm Sáng Tạo returns job_id
9. Edge Function calls ai_image_api('submitted')
10. Edge Function returns { job_id, status: queued }
11. Desktop starts polling /tst-image?action=status
12. Trạm Sáng Tạo processes job (30-60s)
13. Desktop poll: Edge Function polls Trạm Sáng Tạo
14. Trạm Sáng Tạo returns result URL
15. Edge Function calls ai_image_api('complete')
    - Server captures 500 credits
    - Server marks job completed
16. Edge Function returns result URL
17. Desktop downloads image
18. Desktop shows preview in AI Studio
19. User clicks "Dùng kết quả này"
20. Desktop applies to local .audproj texture
21. User exports mod normally
```

### E2E: Giftcode Redemption

```
1. User opens Account
2. User enters giftcode: AUBIZ-PROMO-2024
3. Desktop calls gift_api('redeem')
   - Payload: { deviceCode, giftCode }
4. Server validates code: exists, not expired, not revoked
5. Server checks redemptions < max_redemptions
6. Server checks (code, device) not already redeemed
7. Server grants: 30 days OR 1000 credits
8. Server records gift_redemptions row
9. Server extends subscription OR grants credits
10. Server returns updated entitlement
11. Desktop displays: "Mã quà tặng đã được kích hoạt"
12. Desktop refreshes Account → shows new expiry/credits
```

### E2E: Admin Manual Extension

```
1. Admin opens Admin Portal
2. Admin logs in with MFA
3. Admin searches device by code
4. Admin clicks "Extend Subscription"
5. Admin enters: +30 days, reason: "Compensation for downtime"
6. Admin clicks Confirm → MFA challenge
7. Frontend calls admin RPC with recent auth token
8. Server validates: role=owner, mfa=verified, recent_auth=true
9. Server extends subscription
10. Server records admin_audit event
11. Admin sees updated device profile
12. Next desktop refresh: sees extended subscription
```

---

## Security Boundaries

### Authentication
- Desktop user: Supabase Auth (email + password or OAuth)
- Admin: Supabase Auth + MFA + role check

### Authorization
- Device RPC: service_role context, validates device ownership via user_id
- Payment RPC: service_role context, validates device or admin
- AI RPC: service_role context, validates user + entitlement
- Admin RPC: service_role context, validates admin role + MFA + recent auth

### RLS (Row Level Security)
- Device A cannot read Device B data
- Customer cannot read admin tables
- Admin can read all commercial tables

### Secrets
- Trạm Sáng Tạo API key: Edge Function env var, never sent to desktop
- SePay webhook secret: Edge Function env var for signature verification
- Supabase service role key: backend only
- Device private key: desktop only, encrypted storage
- Entitlement grant signature key: database secret, never exposed

### Network
- All APIs HTTPS only
- Desktop → Supabase: authenticated requests
- SePay → Edge Function: webhook signature verified
- Edge Function → Trạm Sáng Tạo: API key in header

---

## Scalability Notes

**Current scale (MVP):**
- ~100 devices
- ~10 concurrent AI jobs
- ~5 payments/hour
- Single Supabase project
- Single Edge Function deployment

**Bottlenecks:**
- AI provider rate limits (Trạm Sáng Tạo tier)
- Database connections (Supabase pooler handles)
- Edge Function concurrency (Deno Deploy auto-scales)

**Future optimization (PLAN 109+):**
- AI job queue with priority
- Webhook processing queue
- Caching layer for catalog/models
- Database read replicas
- CDN for AI result images

---

## Monitoring & Observability

**Current:**
- Supabase logs: RPC calls, errors
- Edge Function logs: request/response, provider errors
- Database audit tables: commercial events

**Metrics to track:**
- Payment fulfillment latency
- AI job success rate
- Credits reconciliation accuracy
- Webhook processing time
- Desktop entitlement refresh rate

**Alerts:**
- Payment fulfillment failures
- AI provider outage
- Webhook processing errors
- Duplicate transaction attempts
- Admin critical mutations

---

## Testing Coverage

**Unit Tests:**
- Desktop: AccountViewModel, AiStudioViewModel
- Edge Function: model filtering, pricing calculation
- RPC: payment_fulfill_locked, credit_grant idempotency

**Integration Tests:**
- Desktop → Supabase RPC (device, payment, AI)
- Edge Function → Trạm Sáng Tạo API
- Webhook → Edge Function → RPC

**E2E Tests (PLAN 108):**
- Fresh device registration
- Subscription purchase flow
- Credits purchase flow
- Giftcode redemption
- AI generation success/failure
- Payment duplicate webhook
- Admin manual actions
- Offline behavior
- Concurrent operations

---

**Last Updated:** 2026-08-15  
**Plan:** 108 — Commercial End-to-End Integration
