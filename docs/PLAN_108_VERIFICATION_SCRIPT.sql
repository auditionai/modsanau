-- PLAN 108 — Supabase Migration Verification Script
-- Chạy script này trong Supabase Dashboard → SQL Editor để verify migrations deployed

-- ============================================================
-- 1. Check Migration History
-- ============================================================
SELECT
    version,
    name,
    executed_at
FROM supabase_migrations.schema_migrations
ORDER BY version DESC
LIMIT 20;

-- Expected: 14 migrations từ 202608120001 đến 202608150005

-- ============================================================
-- 2. Verify Core Commercial Tables Exist
-- ============================================================

-- Device & Subscription (Plan 86, 104)
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'device_profiles') AS device_profiles_exists;
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'device_keypairs') AS device_keypairs_exists;
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'subscriptions') AS subscriptions_exists;

-- Credits (Plan 60, 85)
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'credit_ledger') AS credit_ledger_exists;
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'credit_wallet') AS credit_wallet_exists;

-- Payment (Plan 107)
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'payment_products') AS payment_products_exists;
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'payment_orders') AS payment_orders_exists;
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'sepay_transactions') AS sepay_transactions_exists;
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'payment_fulfillment_events') AS payment_fulfillment_events_exists;

-- Giftcode (Plan 107)
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'gift_codes') AS gift_codes_exists;
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'gift_redemptions') AS gift_redemptions_exists;

-- AI Jobs (Plan 62, 65, 106)
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'ai_jobs') AS ai_jobs_exists;
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'ai_provider_jobs') AS ai_provider_jobs_exists;

-- Admin (Plan 105, 150001, 150002)
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'admin_users') AS admin_users_exists;
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'admin_audit') AS admin_audit_exists;

-- Audit Trail (Plan 107)
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'payment_audit_events') AS payment_audit_events_exists;
SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'private' AND tablename = 'device_commercial_events') AS device_commercial_events_exists;

-- ============================================================
-- 3. Verify RPC Functions Exist
-- ============================================================

SELECT
    routine_name,
    routine_type,
    routine_schema
FROM information_schema.routines
WHERE routine_schema = 'public'
  AND routine_name IN (
    'device_identity_api',
    'payment_api',
    'gift_api',
    'ai_image_api',
    'desktop_access_api'
  )
ORDER BY routine_name;

-- Expected: 5 RPC functions

-- ============================================================
-- 4. Verify Private Functions (Credit/Payment Logic)
-- ============================================================

SELECT
    routine_name,
    routine_type,
    routine_schema
FROM information_schema.routines
WHERE routine_schema = 'private'
  AND routine_name IN (
    'credit_grant',
    'credit_reserve',
    'credit_capture',
    'credit_release',
    'payment_fulfill_locked',
    'device_entitlement_grant_build',
    'ai_job_enqueue'
  )
ORDER BY routine_name;

-- Expected: 7 private functions

-- ============================================================
-- 5. Verify UNIQUE Constraints (Idempotency Guards)
-- ============================================================

-- Device fingerprint uniqueness
SELECT conname, contype, pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conrelid = 'private.device_profiles'::regclass
  AND contype = 'u'
  AND conname LIKE '%fingerprint%';

-- Payment order idempotency
SELECT conname, contype, pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conrelid = 'private.payment_orders'::regclass
  AND contype = 'u'
  AND conname LIKE '%idempotency%';

-- SePay transaction idempotency
SELECT conname, contype, pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conrelid = 'private.sepay_transactions'::regclass
  AND contype = 'u'
  AND conname LIKE '%provider_transaction%';

-- Payment fulfillment uniqueness
SELECT conname, contype, pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conrelid = 'private.payment_fulfillment_events'::regclass
  AND contype = 'u'
  AND conname LIKE '%payment_order%';

-- AI provider job uniqueness
SELECT conname, contype, pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conrelid = 'private.ai_provider_jobs'::regclass
  AND contype = 'u'
  AND conname LIKE '%provider_job%';

-- Gift redemption uniqueness
SELECT conname, contype, pg_get_constraintdef(oid)
FROM pg_constraint
WHERE conrelid = 'private.gift_redemptions'::regclass
  AND contype = 'u';

-- ============================================================
-- 6. Verify Append-Only Triggers (Credit Ledger)
-- ============================================================

SELECT
    tgname AS trigger_name,
    tgrelid::regclass AS table_name,
    proname AS function_name
FROM pg_trigger
JOIN pg_proc ON pg_trigger.tgfoid = pg_proc.oid
WHERE tgrelid = 'private.credit_ledger'::regclass
  AND tgname LIKE '%prevent%'
ORDER BY tgname;

-- Expected: trigger preventing UPDATE/DELETE

-- ============================================================
-- 7. Verify RLS Policies
-- ============================================================

SELECT
    schemaname,
    tablename,
    policyname,
    permissive,
    roles,
    cmd
FROM pg_policies
WHERE schemaname = 'private'
  AND tablename IN ('device_profiles', 'subscriptions', 'payment_orders', 'ai_jobs')
ORDER BY tablename, policyname;

-- ============================================================
-- 8. Test Credit Operations (Idempotency)
-- ============================================================

-- Generate test user (use actual auth.users.id if available)
DO $$
DECLARE
    test_user_id uuid := '00000000-0000-0000-0000-000000000001'::uuid;
    test_hash text := 'TEST_' || md5(random()::text);
    result record;
BEGIN
    -- Test credit_grant idempotency
    SELECT * INTO result FROM private.credit_grant(
        test_user_id,
        100,
        'test-grant',
        'test-idempotency-key-1',
        test_hash
    );
    RAISE NOTICE 'First grant: transaction_id=%, replayed=%', result.transaction_id, result.replayed;

    -- Retry with same hash → should return replayed=true
    SELECT * INTO result FROM private.credit_grant(
        test_user_id,
        100,
        'test-grant-retry',
        'test-idempotency-key-2',
        test_hash
    );
    RAISE NOTICE 'Second grant (same hash): transaction_id=%, replayed=%', result.transaction_id, result.replayed;

    IF result.replayed THEN
        RAISE NOTICE '✅ Credit idempotency VERIFIED';
    ELSE
        RAISE WARNING '❌ Credit idempotency FAILED: duplicate grant created';
    END IF;
END $$;

-- ============================================================
-- 9. Verify Storage Buckets
-- ============================================================

SELECT
    id,
    name,
    public
FROM storage.buckets
WHERE name IN ('desktop-releases', 'ai-results')
ORDER BY name;

-- Expected: desktop-releases (private), ai-results (private)

-- ============================================================
-- 10. Summary Report
-- ============================================================

SELECT
    'Tables' AS category,
    COUNT(*) AS count
FROM pg_tables
WHERE schemaname = 'private'
  AND tablename IN (
    'device_profiles', 'device_keypairs', 'subscriptions',
    'credit_ledger', 'credit_wallet',
    'payment_products', 'payment_orders', 'sepay_transactions', 'payment_fulfillment_events',
    'gift_codes', 'gift_redemptions',
    'ai_jobs', 'ai_provider_jobs',
    'admin_users', 'admin_audit',
    'payment_audit_events', 'device_commercial_events'
  )

UNION ALL

SELECT
    'RPC Functions' AS category,
    COUNT(*) AS count
FROM information_schema.routines
WHERE routine_schema = 'public'
  AND routine_name IN (
    'device_identity_api',
    'payment_api',
    'gift_api',
    'ai_image_api',
    'desktop_access_api'
  )

UNION ALL

SELECT
    'Private Functions' AS category,
    COUNT(*) AS count
FROM information_schema.routines
WHERE routine_schema = 'private'
  AND routine_name IN (
    'credit_grant',
    'credit_reserve',
    'credit_capture',
    'credit_release',
    'payment_fulfill_locked',
    'device_entitlement_grant_build',
    'ai_job_enqueue'
  )

UNION ALL

SELECT
    'Storage Buckets' AS category,
    COUNT(*) AS count
FROM storage.buckets
WHERE name IN ('desktop-releases', 'ai-results');

-- Expected Results:
-- Tables: 17
-- RPC Functions: 5
-- Private Functions: 7
-- Storage Buckets: 2
