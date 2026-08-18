BEGIN;

CREATE UNIQUE INDEX IF NOT EXISTS ai_jobs_one_active_per_user_idx
    ON private.ai_jobs(user_id)
    WHERE status IN ('Pending', 'Queued', 'Processing', 'ReconciliationRequired');

CREATE OR REPLACE FUNCTION public.ai_user_active_job_api(p_user_id uuid)
RETURNS boolean
LANGUAGE sql
SECURITY DEFINER
SET search_path = pg_catalog, public, private
AS $$
    SELECT EXISTS (
        SELECT 1 FROM private.ai_jobs
        WHERE user_id = p_user_id
          AND status IN ('Pending', 'Queued', 'Processing', 'ReconciliationRequired')
    );
$$;
REVOKE ALL ON FUNCTION public.ai_user_active_job_api(uuid) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.ai_user_active_job_api(uuid) TO service_role;

CREATE OR REPLACE FUNCTION public.ai_job_cancel_api(
    p_user_id uuid, p_job_id uuid, p_finalize boolean DEFAULT false)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, private
AS $$
DECLARE
    job private.ai_jobs%ROWTYPE;
    credit_result record;
BEGIN
    SELECT * INTO job FROM private.ai_jobs
    WHERE job_id = p_job_id AND user_id = p_user_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'AI_JOB_NOT_FOUND'; END IF;
    IF job.status IN ('Completed', 'Failed', 'Cancelled') THEN
        RETURN jsonb_build_object('jobId', job.job_id, 'status', job.status,
            'reservedCredits', job.reserved_credits, 'finalCredits', job.final_credits);
    END IF;
    IF job.status IN ('Pending', 'Queued') OR (job.status = 'Processing' AND p_finalize) THEN
        SELECT * INTO credit_result FROM private.credit_capture(p_user_id, job.reservation_id,
            job.reserved_credits, 'cancel-capture:' || job.job_id::text, job.request_hash);
        UPDATE private.ai_jobs SET status = 'Cancelled', cancel_requested = true,
            final_credits = job.reserved_credits, capture_transaction_id = credit_result.transaction_id,
            cancelled_at = clock_timestamp(), lease_token = NULL, lease_expires_at = NULL,
            updated_at = clock_timestamp() WHERE job_id = job.job_id RETURNING * INTO job;
    ELSIF job.status = 'Processing' THEN
        UPDATE private.ai_jobs SET cancel_requested = true, updated_at = clock_timestamp()
            WHERE job_id = job.job_id RETURNING * INTO job;
    END IF;
    RETURN jsonb_build_object('jobId', job.job_id, 'status', job.status,
        'reservedCredits', job.reserved_credits, 'finalCredits', job.final_credits);
END;
$$;
REVOKE ALL ON FUNCTION public.ai_job_cancel_api(uuid, uuid, boolean) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.ai_job_cancel_api(uuid, uuid, boolean) TO service_role;

COMMIT;
