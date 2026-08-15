# PLAN 108 — Commercial E2E Validation Checklist

## Phân loại

- ✅ **VERIFIED** — Đã kiểm chứng thực tế (screenshot, log, hoặc RPC test thành công)
- ⏳ **NOT VERIFIED** — Chưa kiểm chứng (cần test thực tế)
- 🔧 **REQUIRES FIX** — Phát hiện lỗi cần sửa

---

## 1. Infrastructure Layer

### 1.1 Supabase Migrations Deployed
- [ ] 🔧 `202608120001_plan60_credit_ledger.sql` — NOT DEPLOYED
- [ ] 🔧 `202608120002_plan62_ai_jobs.sql` — NOT DEPLOYED
- [ ] 🔧 `202608120003_plan65_ai_execution.sql` — NOT DEPLOYED
- [ ] 🔧 `202608120004_plan83_supabase_rls_hardening.sql` — NOT DEPLOYED
- [ ] 🔧 `202608120005_plan84_payment_security.sql` — NOT DEPLOYED
- [ ] 🔧 `202608120006_plan85_credit_concurrency_fix.sql` — NOT DEPLOYED
- [ ] 🔧 `202608130001_plan86_device_sessions.sql` — NOT DEPLOYED
- [ ] 🔧 `202608140001_plan104_device_entitlements.sql` — NOT DEPLOYED
- [ ] 🔧 `202608140002_plan105_admin_portal.sql` — NOT DEPLOYED
- [ ] 🔧 `202608150001_admin_portal_v2.sql` — NOT DEPLOYED
- [ ] 🔧 `202608150002_admin_portal_netlify_supabase.sql` — NOT DEPLOYED
- [ ] 🔧 `202608150003_plan106_public_auth_device_download.sql` — NOT DEPLOYED
- [ ] 🔧 `202608150004_plan106_tst_image_web.sql` — NOT DEPLOYED
- [ ] 🔧 `202608150005_plan107_sepay_payments.sql` — NOT DEPLOYED

**Verification Method:** Supabase Dashboard → Database → Schema → check tables exist

**Evidence:** 
- RPC test: `payment_api` không tồn tại trong schema cache
- Curl test: `https://plvuutsjwsawkkrmvigz.supabase.co/rest/v1/rpc/payment_api` → PGRST202 error
- **Status:** CRITICAL BLOCKER — Migrations chưa deployed lên hosted Supabase

---

### 1.2 Edge Functions Deployed
- [ ] 🔧 `/functions/v1/tst-image` — AI generation RPC wrapper — NOT DEPLOYED
- [ ] 🔧 `/functions/v1/payments` — Payment catalog/orders — NOT DEPLOYED
- [ ] 🔧 `/functions/v1/sepay-webhook` — SePay webhook ingestion — NOT DEPLOYED

**Verification Method:** `curl -I https://plvuutsjwsawkkrmvigz.supabase.co/functions/v1/<name>`

**Evidence:**
- Curl test: All Edge Functions return HTTP 404
- Test script: `docs/test_edge_functions.sh` failed on all endpoints
- **Status:** CRITICAL BLOCKER — Edge Functions chưa deployed lên hosted Supabase

---

### 1.3 Admin Portal Deployed
- [ ] ⏳ Netlify: https://modsanau.netlify.app/admin
- [ ] ⏳ Connect to Supabase RPC successfully
- [ ] ⏳ Admin login with MFA

**Verification Method:** Open URL, check network tab for RPC calls

---

## 2. Desktop Configuration Layer

### 2.1 Production Supabase Config
- [x] ✅ Hardcoded URL: `https://plvuutsjwsawkkrmvigz.supabase.co`
- [x] ✅ Hardcoded publishable key in `ProductionSupabaseConfiguration.cs`
- [ ] ⏳ Desktop can resolve auth endpoint
- [ ] ⏳ Desktop can call Edge Functions

**Verified:** Code review confirmed (lines 7-8 in ProductionSupabaseConfiguration.cs)

---

### 2.2 Gateway Environment Variables (Required)
Desktop bootstrap expects these env vars for Device Entitlement:
- [ ] ⏳ `AUDITION_GATEWAY_URL` — Gateway base URL (HTTPS only)
- [ ] ⏳ `AUDITION_ENTITLEMENT_PUBLIC_KEY_PEM` — ECDSA public key for signed grant verification

**Status:** ApplicationBootstrapper.cs:294-306 shows these are REQUIRED. If missing → `UnavailableDeviceEntitlementService`.

**Action Required:** Product Owner must provide Gateway deployment URL + public key PEM.

---

## 3. E2E Scenario Testing

### Scenario 1: Fresh Device Registration
- [ ] ⏳ Launch app with no credential
- [ ] ⏳ App generates RSA keypair
- [ ] ⏳ Calls Gateway `/v1/device/register`
- [ ] ⏳ Receives Device Code (format: `AUBIZ-XXXXXX`)
- [ ] ⏳ Receives signed entitlement grant
- [ ] ⏳ Desktop validates grant signature
- [ ] ⏳ Account shows "Chưa kích hoạt" (no subscription)
- [ ] ⏳ Available credits: 0

**Authority Check:**
- [ ] ⏳ Desktop cannot forge device code
- [ ] ⏳ Signature verification rejects tampered grant

---

### Scenario 2: Subscription Purchase (30-day)
- [ ] ⏳ Desktop calls `/functions/v1/payments/catalog`
- [ ] ⏳ Catalog returns active products
- [ ] ⏳ User selects "Gói 30 ngày · 50,000 đ"
- [ ] ⏳ Desktop calls `POST /functions/v1/payments/orders` with idempotency key
- [ ] ⏳ Server returns QR code URL (vietqr.app domain)
- [ ] ⏳ Desktop displays QR + bank details
- [ ] ⏳ Desktop starts polling `/orders/:id`
- [ ] ⏳ Polling backoff: 4, 5, 7, 10, 15, 20, 30 seconds
- [ ] ⏳ User transfers money via bank app
- [ ] ⏳ SePay webhook arrives at `/functions/v1/sepay-webhook`
- [ ] ⏳ Server validates: amount, account, expiry
- [ ] ⏳ Server calls `payment_fulfill_locked()`
- [ ] ⏳ Subscription extended: `expires_at = greatest(current, now()) + 30 days`
- [ ] ⏳ Desktop poll sees `status: fulfilled`
- [ ] ⏳ Desktop calls Gateway `/v1/device/register` (refresh)
- [ ] ⏳ New grant shows: `SubscriptionStatus: Active`, `CanUseAi: true`
- [ ] ⏳ Account UI: "Đang hoạt động · Còn 30 ngày"

**Idempotency Checks:**
- [ ] ⏳ Retry same idempotency key → returns existing order
- [ ] ⏳ Duplicate webhook → no double fulfillment
- [ ] ⏳ Concurrent fulfillment → only one succeeds

---

### Scenario 3: Credits Purchase (1000 credits)
- [ ] ⏳ User selects "1000 credits · 30,000 đ"
- [ ] ⏳ Order created with unique `order_code`
- [ ] ⏳ Payment fulfilled → `credit_grant()` called
- [ ] ⏳ Ledger entry created with `request_hash`
- [ ] ⏳ Wallet balance += 1000
- [ ] ⏳ Desktop refresh shows available credits: 1000

**Idempotency Checks:**
- [ ] ⏳ Duplicate webhook → `request_hash` collision → no duplicate grant

---

### Scenario 4: AI Generation Success
- [ ] ⏳ User opens AI Studio
- [ ] ⏳ Desktop checks capability: `CanUseAi = true`
- [ ] ⏳ User enters prompt + selects model
- [ ] ⏳ Desktop calls `/functions/v1/tst-image?action=generate`
- [ ] ⏳ Edge Function calls `ai_image_api('prepare')`
- [ ] ⏳ Server checks entitlement (active subscription)
- [ ] ⏳ Server reserves 500 credits via `credit_reserve()`
- [ ] ⏳ Available credits: 1000 → 500
- [ ] ⏳ Edge Function submits to Trạm Sáng Tạo API
- [ ] ⏳ Provider returns `job_id`
- [ ] ⏳ Edge Function calls `ai_image_api('submitted')`
- [ ] ⏳ Desktop starts polling `/tst-image?action=status`
- [ ] ⏳ Provider processes job (30-60s)
- [ ] ⏳ Provider returns result URL
- [ ] ⏳ Edge Function calls `ai_image_api('complete')`
- [ ] ⏳ Server captures 500 credits via `credit_capture()`
- [ ] ⏳ Credits ledger: reserve + capture entries
- [ ] ⏳ Available credits: 500 (final)
- [ ] ⏳ Desktop downloads image
- [ ] ⏳ Preview shown in AI Studio
- [ ] ⏳ User clicks "Dùng kết quả này"
- [ ] ⏳ Applied to local project texture

**Authority Checks:**
- [ ] ⏳ Desktop without subscription → `ai_image_api('prepare')` rejects
- [ ] ⏳ Insufficient credits → reserve fails
- [ ] ⏳ Duplicate `provider_job_id` → UNIQUE violation

---

### Scenario 5: AI Generation Failure
- [ ] ⏳ Provider returns error
- [ ] ⏳ Edge Function calls `ai_image_api('fail')`
- [ ] ⏳ Server releases reserved credits via `credit_release()`
- [ ] ⏳ Available credits: back to original
- [ ] ⏳ Job status: `Failed`
- [ ] ⏳ Desktop shows error message

---

### Scenario 6: Giftcode Redemption
- [ ] ⏳ User enters code: `AUBIZ-PROMO-2024`
- [ ] ⏳ Desktop calls Gateway `/v1/gift-codes/redeem`
- [ ] ⏳ Server validates: exists, not expired, not revoked
- [ ] ⏳ Server checks `max_redemptions` limit
- [ ] ⏳ Server checks device not already redeemed
- [ ] ⏳ Server extends subscription by 30 days OR grants 1000 credits
- [ ] ⏳ Redemption recorded in `gift_redemptions` (UNIQUE constraint)
- [ ] ⏳ Desktop refresh shows updated expiry/credits
- [ ] ⏳ Message: "Mã quà tặng đã được kích hoạt"

**Idempotency Checks:**
- [ ] ⏳ Retry same code → "đã được sử dụng trên thiết bị này"
- [ ] ⏳ Concurrent redemption → only first succeeds

---

### Scenario 7: Admin Manual Subscription Extension
- [ ] ⏳ Admin opens https://modsanau.netlify.app/admin
- [ ] ⏳ Admin logs in with MFA
- [ ] ⏳ Admin searches device by code
- [ ] ⏳ Admin clicks "Extend Subscription"
- [ ] ⏳ Admin enters: +30 days, reason min 8 chars
- [ ] ⏳ MFA challenge required
- [ ] ⏳ RPC validates: role=owner/editor, mfa=verified, recent_auth < 15 min
- [ ] ⏳ Server extends subscription
- [ ] ⏳ Event recorded in `admin_audit`
- [ ] ⏳ Desktop refresh sees extended subscription

---

### Scenario 8: Admin Manual Credit Grant
- [ ] ⏳ Admin grants 500 credits to device
- [ ] ⏳ Reason: min 8 chars
- [ ] ⏳ Server calls `credit_grant()` with correlation_id
- [ ] ⏳ Ledger entry created
- [ ] ⏳ Wallet balance updated
- [ ] ⏳ Audit event recorded
- [ ] ⏳ Desktop refresh shows new balance

**Note:** Admin actions are NOT idempotent by request_hash. Each action is independent.

---

## 4. Recovery & Offline Scenarios

### 4.1 Network Loss During Payment
- [ ] ⏳ Order created, network lost before response
- [ ] ⏳ Desktop shows generic error
- [ ] ⏳ User retries → same idempotency key → returns existing order
- [ ] ⏳ No duplicate order created

---

### 4.2 Desktop Crash During AI Job
- [ ] ⏳ AI job submitted, desktop crashes
- [ ] ⏳ Provider job still running
- [ ] ⏳ Desktop reopens
- [ ] ⏳ AI Studio loads history via `ai_image_api('history')`
- [ ] ⏳ Job appears in "Processing" state
- [ ] ⏳ Desktop polls status
- [ ] ⏳ Result appears when provider completes
- [ ] ⏳ Credits captured exactly once

---

### 4.3 Payment Fulfilled While App Offline
- [ ] ⏳ User creates order, closes app
- [ ] ⏳ Webhook arrives, server fulfills
- [ ] ⏳ Desktop reopens
- [ ] ⏳ Account page refresh → new entitlement grant
- [ ] ⏳ UI shows active subscription

---

### 4.4 Offline Behavior (Supabase Unreachable)
- [ ] ⏳ Desktop uses cached signed grant if not expired
- [ ] ⏳ Commercial mutations fail with clear message
- [ ] ⏳ Local project editing still works
- [ ] ⏳ UI shows "ngoại tuyến" state

---

## 5. Security & Authority Validation

### 5.1 Server Authority Enforcement
- [ ] ⏳ Desktop cannot create subscription without payment
- [ ] ⏳ Desktop cannot grant credits to itself
- [ ] ⏳ Desktop cannot approve giftcode without server
- [ ] ⏳ Desktop cannot submit AI job without entitlement check
- [ ] ⏳ Desktop cannot forge signed entitlement grant

---

### 5.2 Signed Grant Validation
- [ ] ⏳ Valid grant signature → accepted
- [ ] ⏳ Tampered payload → signature mismatch → rejected
- [ ] ⏳ Expired grant → refresh triggered
- [ ] ⏳ Grant with wrong deviceCode → rejected
- [ ] ⏳ Grant with mismatched availableCredits → rejected

---

### 5.3 RLS & Service Role Guards
- [ ] ⏳ Device A cannot read Device B data
- [ ] ⏳ Customer cannot call admin RPC
- [ ] ⏳ `ai_image_api()` rejects non-service_role caller
- [ ] ⏳ `payment_api()` rejects non-service_role caller

---

### 5.4 Idempotency Key Uniqueness
- [ ] ⏳ Payment order: `(device_profile_id, idempotency_key)` UNIQUE
- [ ] ⏳ SePay transaction: `provider_transaction_id` UNIQUE
- [ ] ⏳ Payment fulfillment: `payment_order_id` UNIQUE in `payment_fulfillment_events`
- [ ] ⏳ AI job: `(idempotency_key, request_hash)` replay detection
- [ ] ⏳ Credit grant: `request_hash` UNIQUE
- [ ] ⏳ Giftcode redemption: `(gift_code_id, device_profile_id)` UNIQUE

---

## 6. Concurrency Scenarios

### 6.1 Duplicate Webhook
- [ ] ⏳ Two identical webhooks arrive
- [ ] ⏳ First: inserts `provider_transaction_id`, fulfills
- [ ] ⏳ Second: UNIQUE violation → rejected
- [ ] ⏳ Subscription extended exactly once

---

### 6.2 Concurrent Giftcode Redemption
- [ ] ⏳ Two devices redeem same code simultaneously
- [ ] ⏳ First: inserts redemption, succeeds
- [ ] ⏳ Second: UNIQUE or max_redemptions violation → rejected

---

### 6.3 Admin Action During Payment Fulfillment
- [ ] ⏳ Payment fulfillment transaction A in progress
- [ ] ⏳ Admin extends subscription transaction B concurrently
- [ ] ⏳ Both use `FOR UPDATE` on subscriptions row
- [ ] ⏳ Serialized execution via row lock
- [ ] ⏳ Final expiry: sum of both extensions

---

## 7. Data Consistency Verification

### 7.1 Credit Ledger Immutability
- [ ] ⏳ Ledger trigger rejects UPDATE
- [ ] ⏳ Ledger trigger rejects DELETE
- [ ] ⏳ Wallet balance matches SUM of ledger

---

### 7.2 Audit Trail Completeness
- [ ] ⏳ Every payment fulfillment → `payment_audit_events`
- [ ] ⏳ Every admin action → `admin_audit`
- [ ] ⏳ Every giftcode redemption → `gift_redemptions`
- [ ] ⏳ Every AI job → `ai_jobs` state machine

---

## 8. Edge Cases

### 8.1 Underpayment
- [ ] ⏳ Order: 50,000 VND
- [ ] ⏳ User transfers: 49,000 VND
- [ ] ⏳ Server marks order: `review_required`
- [ ] ⏳ Reason: `UNDERPAYMENT`
- [ ] ⏳ Admin sees review queue
- [ ] ⏳ Desktop poll shows "Thanh toán cần kiểm tra"

---

### 8.2 Late Payment (Order Expired)
- [ ] ⏳ Order TTL: 15 minutes
- [ ] ⏳ User pays after 20 minutes
- [ ] ⏳ Webhook: order expired → `review_required`
- [ ] ⏳ Reason: `LATE_OR_CANCELLED_PAYMENT`

---

### 8.3 AI Provider Outage
- [ ] ⏳ Trạm Sáng Tạo unreachable
- [ ] ⏳ AI Studio shows "dịch vụ đang ngoại tuyến"
- [ ] ⏳ No credits reserved
- [ ] ⏳ Local editor unaffected

---

## 9. Portable Updater Integration

### 9.1 Commercial State Preservation
- [ ] ⏳ Update preserves device credential
- [ ] ⏳ Update preserves cached signed grant
- [ ] ⏳ Update preserves secure workspace
- [ ] ⏳ Post-update: subscription/credits intact

---

## Summary

**Total Checkpoints:** 55+ scenarios across 9 categories

**Current Status:**
- ✅ VERIFIED: 2 items (production config hardcoded)
- ⏳ NOT VERIFIED: 110+ items (need actual testing)
- 🔧 REQUIRES FIX: 17 items (migrations + edge functions not deployed)

**CRITICAL BLOCKERS:**

1. **Supabase Migrations NOT Deployed**
   - All 14 migrations từ `202608120001` đến `202608150005` chưa chạy
   - RPC functions không tồn tại: `payment_api`, `ai_image_api`, `device_identity_api`, etc.
   - Core tables không tồn tại: `device_profiles`, `subscriptions`, `credit_ledger`, `payment_orders`, etc.
   - Evidence: REST API test → PGRST202 "function not found in schema cache"
   - **Impact:** Desktop app không thể connect được backend, toàn bộ commercial stack offline

2. **Edge Functions NOT Deployed**
   - All 3 Edge Functions return HTTP 404:
     - `/functions/v1/tst-image` (AI generation)
     - `/functions/v1/payments` (payment orders)
     - `/functions/v1/sepay-webhook` (webhook ingestion)
   - Evidence: curl test → 404 Not Found
   - **Impact:** Desktop app không thể call AI service, payment service, webhook không nhận được

3. **Gateway URL + ECDSA Public Key Missing**
   - Environment variables chưa được set:
     - `AUDITION_GATEWAY_URL`
     - `AUDITION_ENTITLEMENT_PUBLIC_KEY_PEM`
   - ApplicationBootstrapper.cs:294-306 fallback to `UnavailableDeviceEntitlementService`
   - **Impact:** Device registration, signed grant validation, giftcode redemption toàn bộ không hoạt động

**Deployment Status:**

| Component | Status | Evidence |
|-----------|--------|----------|
| Supabase Migrations | 🔴 NOT DEPLOYED | RPC test failed: PGRST202 |
| Edge Functions | 🔴 NOT DEPLOYED | curl test: HTTP 404 |
| Admin Portal | ⏳ UNKNOWN | URL accessible nhưng chưa test connect |
| Gateway Service | 🔴 NOT CONFIGURED | Env vars missing |
| Desktop App Config | ✅ VERIFIED | Hardcoded Supabase URL/key |

**Next Actions:**

1. **Deploy Supabase Migrations** (CRITICAL)
   - Chạy script: `docs/PLAN_108_VERIFICATION_SCRIPT.sql` trong Supabase Dashboard
   - Hoặc chạy từng migration file trong `supabase/migrations/` theo thứ tự
   - Verify: test RPC `payment_api` qua REST API sau khi deploy

2. **Deploy Edge Functions** (CRITICAL)
   - Deploy từ `supabase/functions/` lên hosted project
   - Command: `supabase functions deploy <name> --project-ref plvuutsjwsawkkrmvigz`
   - Verify: re-run `docs/test_edge_functions.sh`

3. **Configure Gateway** (CRITICAL)
   - Obtain Gateway deployment URL from Product Owner
   - Obtain ECDSA public key PEM from Product Owner
   - Set environment variables cho desktop app

4. **Re-run Validation**
   - After deployment: re-run full checklist
   - Begin E2E Scenario 1: Fresh Device Registration

---

**PLAN 108 STOPPING CONDITION:**

Theo specification, stopping condition là:
> "STOP khi: Đã có báo cáo với từng layer/flow đánh dấu VERIFIED hoặc NOT VERIFIED rõ ràng."

**Current Assessment:**

Infrastructure layer đã được đánh dấu rõ ràng:
- **NOT VERIFIED (với evidence cụ thể):** Migrations, Edge Functions
- **VERIFIED:** Desktop config hardcoded
- **NOT CONFIGURED:** Gateway environment variables

**Recommendation:**

Không thể tiếp tục E2E testing vì backend chưa deployed. Report hiện tại đã đủ để classify từng layer. Tạo final report ngay bây giờ với status NOT VERIFIED cho majority of stack, kèm deployment instructions rõ ràng.

---

**Last Updated:** 2026-08-15  
**Plan:** 108 — Commercial End-to-End Integration Validation
