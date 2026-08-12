BEGIN;

ALTER TABLE private.ai_jobs DROP CONSTRAINT ai_jobs_status_check;
ALTER TABLE private.ai_jobs ADD CONSTRAINT ai_jobs_status_check CHECK (status IN
    ('Pending', 'Queued', 'Processing', 'Completed', 'Failed', 'Cancelled', 'ReconciliationRequired'));

CREATE OR REPLACE FUNCTION private.ai_job_require_reconciliation(
    p_job_id uuid, p_lease_token uuid, p_provider_request_id text, p_error_code text)
RETURNS SETOF private.ai_jobs
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $$
DECLARE
    job private.ai_jobs%ROWTYPE;
BEGIN
    SELECT j.* INTO job FROM private.ai_jobs j WHERE j.job_id = p_job_id FOR UPDATE;
    IF job.status = 'ReconciliationRequired' THEN RETURN NEXT job; RETURN; END IF;
    IF job.status <> 'Processing' OR job.lease_token <> p_lease_token
       OR p_provider_request_id !~ '^[A-Za-z0-9._:-]+$' OR length(p_provider_request_id) > 128
       OR p_error_code !~ '^[A-Z0-9_]+$' OR length(p_error_code) > 128 THEN
        RAISE EXCEPTION 'invalid reconciliation transition'
            USING ERRCODE = 'P0001', CONSTRAINT = 'AI_JOB_TRANSITION_INVALID';
    END IF;
    UPDATE private.ai_jobs SET status = 'ReconciliationRequired',
        provider_request_id = p_provider_request_id, error_code = p_error_code,
        lease_token = NULL, lease_expires_at = NULL, updated_at = clock_timestamp()
        WHERE job_id = job.job_id RETURNING * INTO job;
    RETURN NEXT job;
END;
$$;

CREATE OR REPLACE FUNCTION private.ai_job_cancel(p_user_id uuid, p_job_id uuid)
RETURNS SETOF private.ai_jobs
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $$
DECLARE
    job private.ai_jobs%ROWTYPE;
    credit_result record;
BEGIN
    SELECT j.* INTO job FROM private.ai_jobs j
        WHERE j.job_id = p_job_id AND j.user_id = p_user_id FOR UPDATE;
    IF job.job_id IS NULL THEN
        RAISE EXCEPTION 'job not found'
            USING ERRCODE = 'P0001', CONSTRAINT = 'AI_JOB_NOT_FOUND';
    END IF;
    IF job.status IN ('Completed', 'Failed', 'Cancelled', 'ReconciliationRequired') THEN
        RETURN NEXT job;
        RETURN;
    END IF;
    IF job.status = 'Processing' THEN
        UPDATE private.ai_jobs SET cancel_requested = true, updated_at = clock_timestamp()
            WHERE job_id = job.job_id RETURNING * INTO job;
    ELSE
        SELECT * INTO credit_result FROM private.credit_release(job.user_id, job.reservation_id,
            'cancel:' || job.job_id::text, job.request_hash);
        UPDATE private.ai_jobs SET status = 'Cancelled', cancel_requested = true,
            cancelled_at = clock_timestamp(), updated_at = clock_timestamp()
            WHERE job_id = job.job_id RETURNING * INTO job;
    END IF;
    RETURN NEXT job;
END;
$$;

REVOKE ALL ON FUNCTION private.ai_job_require_reconciliation(uuid, uuid, text, text)
    FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION private.ai_job_require_reconciliation(uuid, uuid, text, text) TO service_role;
REVOKE ALL ON FUNCTION private.ai_job_cancel(uuid, uuid) FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION private.ai_job_cancel(uuid, uuid) TO service_role;

COMMIT;
