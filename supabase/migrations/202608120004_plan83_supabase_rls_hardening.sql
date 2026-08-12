BEGIN;

-- Re-establish least privilege after every user-owned table added by PLAN 60/62/65.
REVOKE ALL ON SCHEMA private FROM PUBLIC, anon, authenticated;
REVOKE ALL ON TABLE private.credit_wallets, private.credit_reservations,
    private.credit_ledger, private.credit_refunds, private.credit_idempotency,
    private.ai_jobs FROM PUBLIC, anon, authenticated;

ALTER TABLE private.credit_wallets ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.credit_wallets FORCE ROW LEVEL SECURITY;
ALTER TABLE private.credit_reservations ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.credit_reservations FORCE ROW LEVEL SECURITY;
ALTER TABLE private.credit_ledger ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.credit_ledger FORCE ROW LEVEL SECURITY;
ALTER TABLE private.credit_refunds ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.credit_refunds FORCE ROW LEVEL SECURITY;
ALTER TABLE private.credit_idempotency ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.credit_idempotency FORCE ROW LEVEL SECURITY;
ALTER TABLE private.ai_jobs ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.ai_jobs FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS credit_wallets_owner_select ON private.credit_wallets;
CREATE POLICY credit_wallets_owner_select ON private.credit_wallets
    FOR SELECT TO authenticated
    USING ((SELECT auth.uid()) = user_id);

DROP POLICY IF EXISTS credit_reservations_owner_select ON private.credit_reservations;
CREATE POLICY credit_reservations_owner_select ON private.credit_reservations
    FOR SELECT TO authenticated
    USING ((SELECT auth.uid()) = user_id);

DROP POLICY IF EXISTS credit_ledger_owner_select ON private.credit_ledger;
CREATE POLICY credit_ledger_owner_select ON private.credit_ledger
    FOR SELECT TO authenticated
    USING ((SELECT auth.uid()) = user_id);

DROP POLICY IF EXISTS credit_refunds_owner_select ON private.credit_refunds;
CREATE POLICY credit_refunds_owner_select ON private.credit_refunds
    FOR SELECT TO authenticated
    USING ((SELECT auth.uid()) = user_id);

DROP POLICY IF EXISTS ai_jobs_owner_select ON private.ai_jobs;
CREATE POLICY ai_jobs_owner_select ON private.ai_jobs
    FOR SELECT TO authenticated
    USING ((SELECT auth.uid()) = user_id);

-- credit_idempotency remains server-only. It is user-keyed but contains replay-control material.
-- No authenticated policy or table/column grant is intentionally created for this table.

GRANT USAGE ON SCHEMA private TO authenticated;
GRANT SELECT (user_id, available_credits, reserved_credits, version, created_at, updated_at)
    ON TABLE private.credit_wallets TO authenticated;
GRANT SELECT (reservation_id, user_id, reserved_credits, captured_credits, status, created_at, closed_at)
    ON TABLE private.credit_reservations TO authenticated;
GRANT SELECT (transaction_id, user_id, entry_type, amount, available_delta, reserved_delta,
    available_after, reserved_after, reservation_id, related_transaction_id, created_at)
    ON TABLE private.credit_ledger TO authenticated;
GRANT SELECT (refund_id, user_id, captured_transaction_id, refund_transaction_id, amount, created_at)
    ON TABLE private.credit_refunds TO authenticated;
GRANT SELECT (job_id, user_id, operation, input_metadata, status, pricing_version,
    reserved_credits, final_credits, output_reference, error_code, attempt_count,
    cancel_requested, created_at, queued_at, processing_at, completed_at, failed_at,
    cancelled_at, updated_at)
    ON TABLE private.ai_jobs TO authenticated;

REVOKE ALL ON FUNCTION private.reject_credit_immutable_mutation(),
    private.assert_credit_request(uuid, text, text),
    private.credit_grant(uuid, bigint, text, text, text),
    private.credit_reserve(uuid, bigint, text, text),
    private.credit_capture(uuid, uuid, bigint, text, text),
    private.credit_release(uuid, uuid, text, text),
    private.credit_refund(uuid, uuid, bigint, text, text),
    private.assert_ai_job_request(uuid, text, text),
    private.ai_job_enqueue(uuid, text, jsonb, bigint, text, text, text),
    private.ai_job_claim(integer),
    private.ai_job_complete(uuid, uuid, bigint, text, text),
    private.ai_job_fail(uuid, uuid, text, text, boolean),
    private.ai_job_cancel(uuid, uuid),
    private.ai_job_require_reconciliation(uuid, uuid, text, text)
    FROM PUBLIC, anon, authenticated;

-- Future private functions created by the same trusted migration owner are not PUBLIC-callable by default.
ALTER DEFAULT PRIVILEGES IN SCHEMA private REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

COMMIT;
