# Commercial Recovery Model — Plan 108

## Overview

Audition AI Mod Studio commercial stack phải chịu được:
- Network loss
- Backend restart
- Desktop crash/close
- Concurrent operations
- Clock tampering
- Partial failures

Mọi commercial mutation đều có recovery path rõ ràng.

---

## Device Identity Recovery

### Scenario: First Registration Interrupted

**Trạng thái:**
- Desktop sent registration request
- Network lost before receiving response

**Server State:**
- Device profile created with UNIQUE `device_fingerprint`
- Public device code assigned

**Recovery:**
- Desktop retry registration with same keypair
- Server detects duplicate fingerprint → returns existing profile
- No duplicate device

**Code:** `device_profile_register()` idempotent via fingerprint.

### Scenario: Credential Lost

**Trạng thái:**
- User deleted app data folder
- Device credential file missing

**Server State:**
- Device profile still exists
- Subscription/credits intact

**Recovery:**
- Desktop detects missing credential
- Re-registers with NEW keypair
- Server creates NEW device profile
- User sees new Device Code
- Old device remains in database (orphaned but safe)
- User contacts support with old Device Code to transfer subscription

**Limitation:** Automatic credential transfer not supported for security. Manual support intervention required.

---

## Subscription Recovery

### Scenario: Payment Fulfilled While App Offline

**Trạng thái:**
- User created payment order
- Desktop closed
- Webhook arrived → server fulfilled
- Desktop reopened

**Server State:**
- `payment_orders.status = 'fulfilled'`
- `subscriptions.expires_at` extended
- `payment_fulfillment_events` recorded

**Recovery:**
- Desktop calls `RefreshAsync()` on Account page activation
- Server returns fresh `DeviceEntitlementSnapshot`
- UI reflects active subscription

**Timeline:** Next account refresh (automatic on page open).

### Scenario: Duplicate Subscription Payment

**Trạng thái:**
- User paid twice for same order (bank retry)
- Two SePay webhooks arrive

**Server State:**
- First webhook: `provider_transaction_id` inserted, fulfillment runs
- Second webhook: `provider_transaction_id` UNIQUE violation → rejected
- RPC returns `replayed: true`

**Recovery:**
- Automatic via database constraint
- No code-level retry needed
- Subscription extended exactly once

**Code:** `sepay_transactions.provider_transaction_id` UNIQUE constraint.

### Scenario: Admin Manual Extension During Payment

**Trạng thái:**
- Payment fulfillment in progress (transaction A)
- Admin extends subscription concurrently (transaction B)

**Server State:**
- Both transactions use `FOR UPDATE` on `subscriptions` row
- Serialized execution via row lock
- Final `expires_at = greatest(current, now()) + total_purchased_duration`

**Recovery:**
- Automatic via transaction isolation
- No lost days

---

## Credits Recovery

### Scenario: Grant Webhook Duplicate

**Trạng thái:**
- Credits payment fulfilled
- Webhook arrives twice (SePay retry)

**Server State:**
- First call: `credit_grant()` succeeds, inserts ledger entry
- Second call: `request_hash` collision → returns `CREDIT_IDEMPOTENT_REPLAY`
- Wallet balance correct

**Recovery:**
- Automatic via `request_hash` UNIQUE constraint
- No duplicate grant

**Code:** `credit_grant()` checks `request_hash` before insert.

### Scenario: Admin Grant Double-Click

**Trạng thái:**
- Admin clicks "Grant 1000 Credits" twice quickly

**Server State:**
- Each admin action uses unique `correlation_id`
- Two separate ledger entries
- Both grants succeed (intentional: admin retries are valid)

**Recovery:**
- Admin sees audit log with two events
- Admin can deduct if mistake
- Append-only ledger preserves full history

**Design:** Admin actions are NOT idempotent by request hash. Each action is independent.

---

## AI Job Recovery

### Scenario: App Crash During AI Generation

**Trạng thái:**
- Desktop submitted AI job
- Provider job running
- Desktop crashed before receiving result

**Server State:**
- `ai_jobs.status = 'Processing'`
- `ai_provider_jobs.provider_job_id` set
- Credits reserved
- Provider job still running

**Recovery:**
1. Desktop reopens
2. AI Studio loads history via `ai_image_api('history')`
3. User sees job in "Processing" state
4. User can poll status or wait
5. Server reconciles provider state on next `ai_image_api('status')` call
6. Result appears when provider completes
7. Credits captured exactly once

**Timeline:** Immediate on history load, completion when provider finishes.

### Scenario: Network Lost After Submit, Before Provider Response

**Trạng thái:**
- Desktop sent `ai_image_api('prepare')` → succeeded, credits reserved
- Desktop sent generate request → provider queued
- Network lost before desktop received `job_id`

**Server State:**
- `ai_jobs` row exists with `status = 'Queued'` or `'Processing'`
- Provider job may or may not have been submitted

**Recovery:**
- Desktop sees no `job_id` → shows generic error
- User retries → new idempotency key → NEW job created
- Original job: if provider job submitted, continues; lease expires after 30 min
- Original job credits: released via lease expiry cleanup (background task)

**Limitation:** Rare edge case. User may create duplicate job. Both jobs will reserve/capture credits independently if both succeed.

**Mitigation:** Edge Function stores provider_job_id immediately after submission to prevent duplicate provider calls.

### Scenario: Provider Returns Success, Desktop Crashes Before Capture

**Trạng thái:**
- Provider job completed with result URL
- Desktop polling → received result
- Desktop crashed before calling Apply

**Server State:**
- `ai_jobs.status = 'Processing'` (not yet Completed)
- `ai_provider_jobs.result_url` NOT yet set
- Credits still reserved

**Recovery:**
1. Next desktop poll: Edge Function fetches provider status again
2. Provider returns same result
3. Edge Function calls `ai_image_api('complete')` with result URL
4. Server captures credits, marks job Completed
5. Desktop sees completed job in history

**Timeline:** Next poll cycle (within seconds).

### Scenario: Concurrent AI Generate (Double-Click)

**Trạng thái:**
- User double-clicks Generate button
- Two requests sent

**Server State:**
- Both requests reach Edge Function with different `idempotency_key` (desktop generates new GUID each time)
- Two separate jobs created
- Two provider jobs submitted
- Two credit reservations

**Recovery:**
- Intentional: each Generate action is independent
- User sees two jobs in history
- Both complete and capture credits

**Design:** Desktop does NOT reuse idempotency key across UI actions. Each Submit generates fresh key.

### Scenario: Provider Job ID Collision

**Trạng thái:**
- Two desktop clients submit AI jobs concurrently
- Both receive same `provider_job_id` from provider (unlikely but possible)

**Server State:**
- First `ai_image_api('submitted')`: sets `ai_provider_jobs.provider_job_id`
- Second: `provider_job_id` UNIQUE constraint violation → raises exception
- Second job fails

**Recovery:**
- Second desktop sees error
- User retries → new job, new provider job
- No duplicate capture

**Code:** `ai_provider_jobs.provider_job_id` UNIQUE constraint.

---

## Payment Recovery

### Scenario: Payment Webhook Arrives Out of Order

**Trạng thái:**
- Webhook 1: `transaction_date = T+5s`
- Webhook 2: `transaction_date = T+0s`
- Webhook 2 arrives first

**Server State:**
- First webhook (W2): matches order, fulfills
- Second webhook (W1): order already has `provider_transaction_id` → `processing_state = 'review_required'`

**Recovery:**
- Automatic safety via `matched_order_id` UNIQUE
- Admin sees review queue
- Admin can mark duplicate as resolved

**Code:** `payment_api('ingest_sepay')` checks existing `provider_transaction_id` before matching.

### Scenario: Underpayment

**Trạng thái:**
- Order price: 50,000 VND
- User transferred: 49,000 VND

**Server State:**
- Webhook: `amount_vnd <> order.price_vnd`
- `payment_orders.status = 'review_required'`
- `payment_orders.review_reason = 'UNDERPAYMENT'`
- Transaction recorded, order NOT fulfilled

**Recovery:**
- Admin sees review queue with amount mismatch
- Admin can: refund, ask user to pay difference, or manually fulfill with adjusted terms
- Desktop polls order status → sees `review_required`
- User sees message: "Thanh toán cần kiểm tra"

**Timeline:** Manual admin action required.

### Scenario: Late Payment (Order Expired)

**Trạng thái:**
- Order created with 15-minute TTL
- User paid after 20 minutes

**Server State:**
- Webhook: order found, but `expires_at <= now()`
- `payment_orders.status = 'review_required'`
- `payment_orders.review_reason = 'LATE_OR_CANCELLED_PAYMENT'`

**Recovery:**
- Same as underpayment: admin review
- Admin can manually fulfill if payment valid

### Scenario: Desktop Polling Timeout

**Trạng thái:**
- Desktop polling for 15 minutes
- Payment not yet received
- Polling stops

**Server State:**
- Order remains `waiting_payment`
- If payment arrives later, webhook fulfills normally

**Recovery:**
- Desktop shows: "Đã dừng cập nhật tự động"
- User clicks Refresh (Làm mới)
- Desktop calls `GetOrderAsync()` → server returns current status
- If fulfilled, desktop shows success

**Timeline:** Manual user action or next Account page activation.

---

## Giftcode Recovery

### Scenario: Concurrent Redemption (Same Code, Two Devices)

**Trạng thái:**
- Two users with same giftcode (shared publicly)
- Both redeem simultaneously

**Server State:**
- First redemption: inserts `gift_redemptions` row, extends subscription
- Second redemption: `(gift_code_id, device_profile_id)` UNIQUE violation OR `max_redemptions` exceeded
- Second call raises exception

**Recovery:**
- Automatic via database constraint
- Second desktop sees error: "Mã quà tặng không hợp lệ, đã hết hạn hoặc đã được sử dụng"

**Code:** `gift_redemptions.UNIQUE(gift_code_id, device_profile_id)` + trigger enforcing `max_redemptions`.

### Scenario: Redemption Network Timeout

**Trạng thái:**
- Desktop sent redemption request
- Response timeout
- User unsure if succeeded

**Server State:**
- Redemption may or may not have committed

**Recovery:**
- Desktop shows error
- User retries with same code
- If succeeded: "đã được sử dụng trên thiết bị này"
- If failed: retries safely

**Design:** Giftcode redemption is idempotent per device.

---

## Offline / Outage Behavior

### Supabase Outage

**State:**
- Account backend unreachable

**Desktop:**
- Uses cached `DeviceEntitlementSnapshot` if not expired
- Commercial mutations fail with clear message
- Local project editing unaffected
- UI shows "ngoại tuyến" state

**Recovery:**
- Automatic when backend returns
- Desktop retries with exponential backoff

### AI Provider Outage

**State:**
- Trạm Sáng Tạo unreachable

**Desktop:**
- AI Studio shows "dịch vụ đang ngoại tuyến"
- No credits reserved
- Local editor unaffected

**Recovery:**
- User waits and retries
- Server health check can detect provider status

### Payment Service Outage

**State:**
- Order creation succeeds (uses database only)
- Payment instructions shown
- Webhook processing may be delayed

**Desktop:**
- Polling continues with backoff
- Shows "delayed verification" if no update

**Recovery:**
- Webhook processes when service returns
- Desktop next poll sees updated status

---

## Data Consistency Rules

1. **One payment order → max one fulfillment event**
   - `payment_fulfillment_events.payment_order_id` UNIQUE
   - `payment_fulfill_locked()` uses `FOR UPDATE`

2. **One AI job → max one credit capture**
   - `ai_jobs.capture_transaction_id` UNIQUE
   - `credit_capture()` checks existing transaction

3. **One AI job → max one provider job**
   - `ai_provider_jobs.provider_job_id` UNIQUE
   - Replay detection via idempotency key

4. **One giftcode → bounded redemptions**
   - `gift_redemptions` + trigger enforcing `max_redemptions`
   - Atomic increment under transaction

5. **Credit ledger append-only**
   - Trigger rejects UPDATE/DELETE
   - All mutations via stored procedures

6. **Subscription time authority: server**
   - Desktop clock ignored
   - `expires_at = greatest(current_expiry, clock_timestamp()) + duration`

7. **Device identity unique**
   - `device_fingerprint` UNIQUE prevents duplicates
   - Keypair collision astronomically unlikely

---

## Testing Strategy

### Failure Injection Points

1. Network loss after request sent, before response received
2. Backend restart during transaction
3. Desktop crash during polling
4. Concurrent mutation (double-click, double webhook)
5. Clock tampering (desktop time change)
6. Invalid/expired/tampered credentials
7. Partial payment (under/over)
8. Expired order payment
9. Provider failure after credit reservation
10. Provider timeout with unknown outcome

### Verification

Each failure mode must have:
- Clear server state after recovery
- No duplicate grant/capture
- No lost payment/credits
- Audit trail complete
- User-facing error message

---

**Last Updated:** 2026-08-15  
**Plan:** 108 — Commercial End-to-End Integration
