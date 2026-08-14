BEGIN;

CREATE TABLE private.ai_jobs (
    job_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL,
    operation text NOT NULL CHECK (operation IN
        ('Generate', 'Edit', 'Inpaint', 'Outpaint', 'RemoveObject', 'ReplaceObject', 'Upscale')),
    input_metadata jsonb NOT NULL CHECK (jsonb_typeof(input_metadata) = 'object'
        AND octet_length(input_metadata::text) BETWEEN 2 AND 16384),
    status text NOT NULL CHECK (status IN
        ('Pending', 'Queued', 'Processing', 'Completed', 'Failed', 'Cancelled')),
    pricing_version varchar(64) NOT NULL,
    reserved_credits bigint NOT NULL CHECK (reserved_credits > 0),
    final_credits bigint CHECK (final_credits > 0 AND final_credits <= reserved_credits),
    reservation_id uuid NOT NULL UNIQUE REFERENCES private.credit_reservations(reservation_id),
    capture_transaction_id uuid REFERENCES private.credit_ledger(transaction_id),
    output_reference varchar(256),
    provider_request_id varchar(128),
    error_code varchar(128),
    idempotency_key varchar(128) NOT NULL,
    request_hash char(64) NOT NULL CHECK (request_hash ~ '^[0-9A-F]{64}$'),
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count BETWEEN 0 AND 10),
    max_attempts integer NOT NULL DEFAULT 3 CHECK (max_attempts BETWEEN 1 AND 10),
    cancel_requested boolean NOT NULL DEFAULT false,
    lease_token uuid,
    lease_expires_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    queued_at timestamptz,
    processing_at timestamptz,
    completed_at timestamptz,
    failed_at timestamptz,
    cancelled_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (user_id, idempotency_key),
    CHECK ((status = 'Processing') = (lease_token IS NOT NULL AND lease_expires_at IS NOT NULL)),
    CHECK (output_reference IS NULL OR output_reference ~ '^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$'),
    CHECK (provider_request_id IS NULL OR provider_request_id ~ '^[A-Za-z0-9._:-]+$'),
    CHECK (error_code IS NULL OR error_code ~ '^[A-Z0-9_]+$')
);

CREATE INDEX ai_jobs_owner_history_idx ON private.ai_jobs(user_id, created_at DESC);
CREATE INDEX ai_jobs_queue_idx ON private.ai_jobs(status, queued_at)
    WHERE status IN ('Queued', 'Processing');

CREATE OR REPLACE FUNCTION private.assert_ai_job_request(
    p_user_id uuid, p_idempotency_key text, p_request_hash text)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $$
BEGIN
    IF p_user_id IS NULL OR p_user_id = '00000000-0000-0000-0000-000000000000'::uuid
       OR p_idempotency_key IS NULL OR length(p_idempotency_key) NOT BETWEEN 1 AND 128
       OR p_idempotency_key !~ '^[A-Za-z0-9._:-]+$'
       OR p_request_hash !~ '^[0-9A-F]{64}$' THEN
        RAISE EXCEPTION 'invalid AI job request'
            USING ERRCODE = 'P0001', CONSTRAINT = 'AI_JOB_REQUEST_INVALID';
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION private.ai_job_enqueue(
    p_user_id uuid, p_operation text, p_input_metadata jsonb, p_reserved_credits bigint,
    p_pricing_version text, p_idempotency_key text, p_request_hash text)
RETURNS SETOF private.ai_jobs
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $$
DECLARE
    existing private.ai_jobs%ROWTYPE;
    created private.ai_jobs%ROWTYPE;
    credit_result record;
BEGIN
    PERFORM private.assert_ai_job_request(p_user_id, p_idempotency_key, p_request_hash);
    IF p_operation NOT IN ('Generate', 'Edit', 'Inpaint', 'Outpaint', 'RemoveObject', 'ReplaceObject', 'Upscale')
       OR p_reserved_credits <= 0 OR length(p_pricing_version) NOT BETWEEN 1 AND 64
       OR p_pricing_version !~ '^[A-Za-z0-9._-]+$'
       OR jsonb_typeof(p_input_metadata) <> 'object'
       OR octet_length(p_input_metadata::text) NOT BETWEEN 2 AND 16384 THEN
        RAISE EXCEPTION 'invalid AI job request'
            USING ERRCODE = 'P0001', CONSTRAINT = 'AI_JOB_REQUEST_INVALID';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended(p_user_id::text || ':ai-job:' || p_idempotency_key, 0));
    SELECT j.* INTO existing FROM private.ai_jobs j
        WHERE j.user_id = p_user_id AND j.idempotency_key = p_idempotency_key;
    IF existing.job_id IS NOT NULL THEN
        IF existing.request_hash <> p_request_hash THEN
            RAISE EXCEPTION 'AI job idempotency conflict'
                USING ERRCODE = 'P0001', CONSTRAINT = 'AI_JOB_IDEMPOTENCY_CONFLICT';
        END IF;
        RETURN NEXT existing;
        RETURN;
    END IF;

    SELECT * INTO credit_result FROM private.credit_reserve(
        p_user_id, p_reserved_credits, 'job:' || p_idempotency_key, p_request_hash);
    IF credit_result.status_code NOT IN ('CREDIT_APPLIED', 'CREDIT_IDEMPOTENT_REPLAY')
       OR credit_result.reservation_id IS NULL THEN
        RAISE EXCEPTION 'credit reservation unavailable'
            USING ERRCODE = 'P0001', CONSTRAINT = 'AI_JOB_RESERVATION_FAILED';
    END IF;
    INSERT INTO private.ai_jobs(user_id, operation, input_metadata, status, pricing_version,
        reserved_credits, reservation_id, idempotency_key, request_hash)
    VALUES (p_user_id, p_operation, p_input_metadata, 'Pending', p_pricing_version,
        p_reserved_credits, credit_result.reservation_id, p_idempotency_key, p_request_hash)
    RETURNING * INTO created;
    UPDATE private.ai_jobs SET status = 'Queued', queued_at = clock_timestamp(),
        updated_at = clock_timestamp() WHERE job_id = created.job_id RETURNING * INTO created;
    RETURN NEXT created;
END;
$$;

CREATE OR REPLACE FUNCTION private.ai_job_claim(p_lease_seconds integer)
RETURNS SETOF private.ai_jobs
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $$
DECLARE
    claimed private.ai_jobs%ROWTYPE;
    credit_result record;
BEGIN
    IF p_lease_seconds NOT BETWEEN 5 AND 3600 THEN
        RAISE EXCEPTION 'invalid lease'
            USING ERRCODE = 'P0001', CONSTRAINT = 'AI_JOB_LEASE_INVALID';
    END IF;
    SELECT j.* INTO claimed FROM private.ai_jobs j
        WHERE j.status = 'Queued'
           OR (j.status = 'Processing' AND j.lease_expires_at <= clock_timestamp())
        ORDER BY COALESCE(j.queued_at, j.created_at), j.job_id
        FOR UPDATE SKIP LOCKED LIMIT 1;
    IF claimed.job_id IS NULL THEN RETURN; END IF;
    IF claimed.status = 'Processing' AND claimed.cancel_requested THEN
        SELECT * INTO credit_result FROM private.credit_release(claimed.user_id, claimed.reservation_id,
            'cancel:' || claimed.job_id::text, claimed.request_hash);
        UPDATE private.ai_jobs SET status = 'Cancelled', cancelled_at = clock_timestamp(),
            lease_token = NULL, lease_expires_at = NULL, updated_at = clock_timestamp()
            WHERE job_id = claimed.job_id RETURNING * INTO claimed;
        RETURN NEXT claimed;
        RETURN;
    END IF;
    IF claimed.status = 'Processing' AND claimed.attempt_count >= claimed.max_attempts THEN
        SELECT * INTO credit_result FROM private.credit_release(claimed.user_id, claimed.reservation_id,
            'fail:' || claimed.job_id::text, claimed.request_hash);
        UPDATE private.ai_jobs SET status = 'Failed', error_code = 'AI_JOB_LEASE_EXHAUSTED',
            failed_at = clock_timestamp(), lease_token = NULL, lease_expires_at = NULL,
            updated_at = clock_timestamp() WHERE job_id = claimed.job_id RETURNING * INTO claimed;
        RETURN NEXT claimed;
        RETURN;
    END IF;
    UPDATE private.ai_jobs SET status = 'Processing', attempt_count = attempt_count + 1,
        processing_at = COALESCE(processing_at, clock_timestamp()), lease_token = gen_random_uuid(),
        lease_expires_at = clock_timestamp() + make_interval(secs => p_lease_seconds),
        updated_at = clock_timestamp() WHERE job_id = claimed.job_id RETURNING * INTO claimed;
    RETURN NEXT claimed;
END;
$$;

CREATE OR REPLACE FUNCTION private.ai_job_complete(
    p_job_id uuid, p_lease_token uuid, p_final_credits bigint,
    p_output_reference text, p_provider_request_id text)
RETURNS SETOF private.ai_jobs
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $$
DECLARE
    job private.ai_jobs%ROWTYPE;
    credit_result record;
BEGIN
    SELECT j.* INTO job FROM private.ai_jobs j WHERE j.job_id = p_job_id FOR UPDATE;
    IF job.status = 'Completed' THEN
        IF job.final_credits <> p_final_credits OR job.output_reference <> p_output_reference
           OR job.provider_request_id <> p_provider_request_id THEN
            RAISE EXCEPTION 'completion replay conflict'
                USING ERRCODE = 'P0001', CONSTRAINT = 'AI_JOB_IDEMPOTENCY_CONFLICT';
        END IF;
        RETURN NEXT job;
        RETURN;
    END IF;
    IF job.status <> 'Processing' OR job.lease_token <> p_lease_token
       OR job.lease_expires_at <= clock_timestamp() OR job.cancel_requested
       OR p_final_credits <= 0 OR p_final_credits > job.reserved_credits
       OR p_output_reference !~ '^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$'
       OR length(p_output_reference) > 256
       OR p_provider_request_id !~ '^[A-Za-z0-9._:-]+$' OR length(p_provider_request_id) > 128 THEN
        RAISE EXCEPTION 'invalid completion transition'
            USING ERRCODE = 'P0001', CONSTRAINT = 'AI_JOB_TRANSITION_INVALID';
    END IF;
    SELECT * INTO credit_result FROM private.credit_capture(job.user_id, job.reservation_id,
        p_final_credits, 'capture:' || job.job_id::text, job.request_hash);
    UPDATE private.ai_jobs SET status = 'Completed', final_credits = p_final_credits,
        capture_transaction_id = credit_result.transaction_id, output_reference = p_output_reference,
        provider_request_id = p_provider_request_id, completed_at = clock_timestamp(),
        lease_token = NULL, lease_expires_at = NULL, updated_at = clock_timestamp()
        WHERE job_id = job.job_id RETURNING * INTO job;
    RETURN NEXT job;
END;
$$;

CREATE OR REPLACE FUNCTION private.ai_job_fail(
    p_job_id uuid, p_lease_token uuid, p_provider_request_id text,
    p_error_code text, p_retryable boolean)
RETURNS SETOF private.ai_jobs
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $$
DECLARE
    job private.ai_jobs%ROWTYPE;
    credit_result record;
BEGIN
    SELECT j.* INTO job FROM private.ai_jobs j WHERE j.job_id = p_job_id FOR UPDATE;
    IF job.status IN ('Failed', 'Cancelled') THEN RETURN NEXT job; RETURN; END IF;
    IF job.status <> 'Processing' OR job.lease_token <> p_lease_token
       OR p_provider_request_id !~ '^[A-Za-z0-9._:-]+$' OR length(p_provider_request_id) > 128
       OR p_error_code !~ '^[A-Z0-9_]+$' OR length(p_error_code) > 128 THEN
        RAISE EXCEPTION 'invalid failure transition'
            USING ERRCODE = 'P0001', CONSTRAINT = 'AI_JOB_TRANSITION_INVALID';
    END IF;
    IF p_retryable AND NOT job.cancel_requested AND job.attempt_count < job.max_attempts THEN
        UPDATE private.ai_jobs SET status = 'Queued', error_code = p_error_code,
            provider_request_id = p_provider_request_id,
            queued_at = clock_timestamp(), lease_token = NULL, lease_expires_at = NULL,
            updated_at = clock_timestamp() WHERE job_id = job.job_id RETURNING * INTO job;
    ELSE
        SELECT * INTO credit_result FROM private.credit_release(job.user_id, job.reservation_id,
            CASE WHEN job.cancel_requested THEN 'cancel:' ELSE 'fail:' END || job.job_id::text,
            job.request_hash);
        UPDATE private.ai_jobs SET status = CASE WHEN cancel_requested THEN 'Cancelled' ELSE 'Failed' END,
            error_code = p_error_code, provider_request_id = p_provider_request_id,
            failed_at = CASE WHEN cancel_requested THEN NULL ELSE clock_timestamp() END,
            cancelled_at = CASE WHEN cancel_requested THEN clock_timestamp() ELSE NULL END,
            lease_token = NULL, lease_expires_at = NULL, updated_at = clock_timestamp()
            WHERE job_id = job.job_id RETURNING * INTO job;
    END IF;
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
    IF job.status IN ('Completed', 'Failed', 'Cancelled') THEN RETURN NEXT job; RETURN; END IF;
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

ALTER TABLE private.ai_jobs ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON TABLE private.ai_jobs FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION private.assert_ai_job_request(uuid, text, text) FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION private.ai_job_enqueue(uuid, text, jsonb, bigint, text, text, text) FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION private.ai_job_claim(integer) FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION private.ai_job_complete(uuid, uuid, bigint, text, text) FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION private.ai_job_fail(uuid, uuid, text, text, boolean) FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION private.ai_job_cancel(uuid, uuid) FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION private.ai_job_enqueue(uuid, text, jsonb, bigint, text, text, text) TO service_role;
GRANT EXECUTE ON FUNCTION private.ai_job_claim(integer) TO service_role;
GRANT EXECUTE ON FUNCTION private.ai_job_complete(uuid, uuid, bigint, text, text) TO service_role;
GRANT EXECUTE ON FUNCTION private.ai_job_fail(uuid, uuid, text, text, boolean) TO service_role;
GRANT EXECUTE ON FUNCTION private.ai_job_cancel(uuid, uuid) TO service_role;
GRANT SELECT ON TABLE private.ai_jobs TO service_role;

COMMIT;
