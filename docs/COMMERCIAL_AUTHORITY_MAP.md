# Commercial Authority Map — Plan 108

## Principle

**SERVER AUTHORITY. CLIENT CACHE.**

Desktop app không thể tự cấp cho mình:
- Subscription
- Credits
- Entitlement
- Admin privileges
- Payment success
- AI billing state

## Authority Matrix

| Domain | Authority | Implementation | Idempotency Key |
|--------|-----------|----------------|-----------------|
| **Device Identity** | Server | `device_profile_register()` RPC | `device_keypair.public_key_fingerprint` UNIQUE |
| **Device Credential** | Server issues, Desktop stores | JWT-style signed grant in `DeviceEntitlementSnapshot.SignedGrant` | N/A |
| **Subscription Status** | Server | `private.subscriptions` table, enforced by RLS + RPC | Device profile FK |
| **Subscription Extension** | Server | `payment_fulfill_locked()` OR `admin_extend_subscription()` | `payment_order_id` UNIQUE OR admin audit |
| **Entitlement Grant** | Server | `device_entitlement_grant_build()` returns signed token | N/A |
| **Credits Balance** | Server | `private.credit_wallet` table (append-only ledger) | N/A |
| **Credit Grant** | Server | `credit_grant()` with `request_hash` | `request_hash` UNIQUE per idempotency_key |
| **Credit Reserve** | Server | `credit_reserve()` within `ai_job_enqueue()` | `ai_jobs.reservation_id` UNIQUE |
| **Credit Capture** | Server | `credit_capture()` within `ai_image_api('complete')` | `ai_jobs.capture_transaction_id` UNIQUE |
| **Credit Release** | Server | `credit_release()` within `ai_image_api('fail')` | Transaction-scoped |
| **Giftcode Validity** | Server | `private.gift_codes` table | `code_value` UNIQUE |
| **Giftcode Redemption** | Server | `gift_redeem()` RPC | `(gift_code_id, device_profile_id)` UNIQUE |
| **Payment Order** | Server | `payment_api('create_order')` | `(device_profile_id, idempotency_key)` UNIQUE |
| **Payment Transaction** | Server | `payment_api('ingest_sepay')` | `provider_transaction_id` UNIQUE |
| **Payment Fulfillment** | Server | `payment_fulfill_locked()` with advisory lock | `payment_order_id` → max one fulfillment event |
| **AI Job** | Server | `ai_image_api('prepare')` | `(idempotency_key, request_hash)` replay detection |
| **AI Provider Job** | Server | `ai_provider_jobs.provider_job_id` UNIQUE | `provider_job_id` UNIQUE |
| **Admin Role** | Server | `private.admin_users` with MFA state | Session + recent auth |
| **Audit Event** | Server | Append-only `admin_audit`, `payment_audit_events`, `device_commercial_events` | `correlation_id` UNIQUE |

## Key Constraints

### Device
- `device_profiles.device_fingerprint` UNIQUE → no duplicate device registration
- `device_profiles.public_device_code` UNIQUE → public identifier
- `device_profiles.auth_user_id` UNIQUE → one device per user

### Subscription
- `subscriptions.device_profile_id` PRIMARY KEY → one active subscription per device
- Extension logic: `expires_at = greatest(current_expiry, now()) + purchased_duration`
- No clock authority from desktop

### Credits
- `credit_ledger` append-only, guarded by trigger
- `credit_wallet` derived state, updated only via ledger insert
- All mutations via `credit_grant()`, `credit_reserve()`, `credit_capture()`, `credit_release()`
- Request hash prevents duplicate grants: `SHA256(order_id || amount)` or `SHA256(job_id || params)`

### Payment
- `payment_orders.order_code` UNIQUE
- `payment_orders.(device_profile_id, idempotency_key)` UNIQUE → replay safe
- `sepay_transactions.provider_transaction_id` UNIQUE → webhook idempotency
- `sepay_transactions.matched_order_id` UNIQUE → one transaction per order
- `payment_fulfillment_events.payment_order_id` UNIQUE → one fulfillment per order
- `payment_fulfill_locked()` uses `FOR UPDATE` + advisory lock
- Concurrent webhook: database constraint rejects duplicate

### Giftcode
- `gift_codes.code_value` UNIQUE
- `gift_redemptions.(gift_code_id, device_profile_id)` UNIQUE → one redemption per device
- `gift_codes.max_redemptions` enforced by trigger
- Concurrent redemption: transaction isolation + constraint

### AI Jobs
- `ai_jobs.job_id` PRIMARY KEY
- `ai_jobs.reservation_id` UNIQUE → one reserve per job
- `ai_jobs.capture_transaction_id` UNIQUE → one capture per job
- `ai_provider_jobs.provider_job_id` UNIQUE → no duplicate provider submission
- Idempotency via `(idempotency_key, request_hash)` replay detection in `ai_job_enqueue()`
- Capture/release exactly once per job via transaction + UNIQUE constraint

### Admin
- `admin_users.admin_user_id` PRIMARY KEY
- Role: `owner`, `editor`, `auditor`
- MFA state: `MFA_VERIFIED` required for sensitive actions
- Recent auth required for critical commercial mutations

## Desktop Client Responsibilities

Desktop CANNOT:
- Create subscription
- Grant credits
- Fulfill payment
- Approve giftcode
- Submit AI job without entitlement check
- Forge entitlement token

Desktop CAN:
- Register device identity (server validates)
- Store signed entitlement grant (read-only cache)
- Display cached subscription/credits (always refresh on critical path)
- Create payment order (server validates device + product)
- Poll payment order status (server authoritative)
- Redeem giftcode (server validates)
- Submit AI job request (server reserves credits, checks entitlement)
- Poll AI job status (server authoritative)

Desktop MUST:
- Use cached entitlement grant until `GrantExpiresAt`
- Refresh entitlement after: payment fulfilled, giftcode redeemed, admin action
- Never assume payment success without server confirmation
- Never assume AI job result without server completion
- Handle offline/outage gracefully with cached state
- Show user-facing errors, not raw SQL codes

## Offline Behavior

When account backend unavailable:
- Desktop uses cached signed entitlement grant if not expired
- Commercial mutations (payment, giftcode, AI) fail closed
- Local project editing remains available
- UI shows clear offline state, no fake success

When AI provider unavailable:
- Desktop detects via API error
- No credits reserved
- Clear error message
- Local editor unaffected

When payment service unavailable:
- Order creation fails
- Existing order status polling retries with backoff
- No fake fulfillment

## Recovery Model

### Payment webhook lost
- Server fulfills on webhook arrival
- Desktop polls order status
- Next app reconnect sees fulfilled state

### AI job interrupted
- Desktop reopens: polls existing job via `ai_image_api('status')`
- Server reconciles provider job state
- No duplicate submission (idempotency key + provider_job_id UNIQUE)

### App crash during commercial task
- All server state persisted
- Desktop restart: refresh entitlement, poll orders, reconcile AI jobs
- No data loss

### Concurrent webhook
- Database constraint: `provider_transaction_id` UNIQUE
- Second webhook: replayed, no duplicate fulfillment

### Clock tampering
- Subscription expiry: server time authority
- Entitlement grant: server issues with server-side expiry
- Payment expiry: server time
- No desktop clock trust

## Audit Trail

Every privileged action recorded:
- Admin subscription change → `admin_audit`
- Admin credit mutation → `admin_audit`
- Payment fulfillment → `payment_audit_events` + `device_commercial_events`
- Giftcode redemption → `gift_redemptions` + `device_commercial_events`
- Device block/unblock → `device_commercial_events`
- AI job lifecycle → `ai_jobs` state machine

Audit tables: append-only, no UPDATE/DELETE allowed via trigger.

## Verification Checklist

- [ ] Device A cannot read Device B data (RLS)
- [ ] Customer cannot call admin RPC (service_role guard)
- [ ] Duplicate webhook → one fulfillment
- [ ] Concurrent giftcode → one redemption per device
- [ ] Concurrent AI generate → one provider job
- [ ] Payment underpayment → `review_required`
- [ ] Expired subscription → AI denied server-side
- [ ] Blocked device → all paid features denied
- [ ] Offline desktop → cached entitlement usable until expiry
- [ ] Desktop clock change → subscription authority unchanged
- [ ] Forged entitlement token → server rejects (signature mismatch)
- [ ] Admin without MFA → sensitive actions denied

---

**Last Updated:** 2026-08-15  
**Plan:** 108 — Commercial End-to-End Integration
