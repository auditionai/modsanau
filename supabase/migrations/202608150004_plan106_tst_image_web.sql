BEGIN;

CREATE TABLE private.ai_provider_jobs (
    job_id uuid PRIMARY KEY REFERENCES private.ai_jobs(job_id) ON DELETE CASCADE,
    user_id uuid NOT NULL,
    provider_model varchar(64) NOT NULL CHECK (provider_model ~ '^[a-z0-9._-]{1,64}$'),
    provider_job_id varchar(128) UNIQUE,
    provider_settings jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(provider_settings) = 'object'),
    result_url text,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK (provider_job_id IS NULL OR provider_job_id ~ '^[A-Za-z0-9._:-]{1,128}$'),
    CHECK (result_url IS NULL OR length(result_url) BETWEEN 8 AND 2048)
);

CREATE INDEX ai_provider_jobs_owner_idx ON private.ai_provider_jobs(user_id, created_at DESC);
ALTER TABLE private.ai_provider_jobs ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.ai_provider_jobs FORCE ROW LEVEL SECURITY;
REVOKE ALL ON private.ai_provider_jobs FROM PUBLIC, anon, authenticated;
GRANT SELECT, INSERT, UPDATE ON private.ai_provider_jobs TO service_role;

CREATE OR REPLACE FUNCTION public.ai_image_api(action text, payload jsonb DEFAULT '{}'::jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, private
AS $function$
DECLARE
    actor_id uuid := NULLIF(payload->>'userId', '')::uuid;
    job_id_value uuid := NULLIF(payload->>'jobId', '')::uuid;
    lease_value uuid;
    job private.ai_jobs%ROWTYPE;
    provider private.ai_provider_jobs%ROWTYPE;
    credit_result record;
    profile record;
    cost bigint;
    request_hash text;
    idempotency_key text;
    model_id text;
    provider_job_id text;
    error_code text;
    result_url text;
BEGIN
    IF current_setting('request.jwt.claim.role', true) IS DISTINCT FROM 'service_role' THEN
        RAISE EXCEPTION 'AI_SERVICE_ROLE_REQUIRED' USING ERRCODE = '42501';
    END IF;
    IF actor_id IS NULL THEN RAISE EXCEPTION 'AUTH_REQUIRED' USING ERRCODE = '42501'; END IF;

    IF action = 'prepare' THEN
        cost := NULLIF(payload->>'creditCost', '')::bigint;
        request_hash := upper(COALESCE(payload->>'requestHash', ''));
        idempotency_key := COALESCE(payload->>'idempotencyKey', '');
        model_id := COALESCE(payload->>'model', '');
        IF cost IS NULL OR cost <= 0 OR cost > 1000000
           OR request_hash !~ '^[0-9A-F]{64}$'
           OR idempotency_key !~ '^[A-Za-z0-9._:-]{1,128}$'
           OR model_id !~ '^[a-z0-9._-]{1,64}$' THEN
            RAISE EXCEPTION 'AI_REQUEST_INVALID';
        END IF;
        SELECT * INTO profile FROM private.device_profile_register(actor_id);
        IF profile.device_status <> 'active' THEN RAISE EXCEPTION 'DEVICE_INACTIVE' USING ERRCODE='42501'; END IF;
        IF NOT profile.can_use_ai THEN RAISE EXCEPTION 'SUBSCRIPTION_EXPIRED' USING ERRCODE='42501'; END IF;
        SELECT * INTO job FROM private.ai_job_enqueue(actor_id, 'Generate',
            jsonb_build_object('model', model_id, 'settings', COALESCE(payload->'settings','{}'::jsonb)),
            cost, left(COALESCE(payload->>'pricingVersion','tst-live'),64), idempotency_key, request_hash);
        INSERT INTO private.ai_provider_jobs(job_id,user_id,provider_model,provider_settings)
        VALUES(job.job_id,actor_id,model_id,COALESCE(payload->'settings','{}'::jsonb))
        ON CONFLICT(job_id) DO NOTHING;
        SELECT * INTO provider FROM private.ai_provider_jobs WHERE job_id=job.job_id;
        RETURN jsonb_build_object('jobId',job.job_id,'status',job.status,'providerJobId',provider.provider_job_id,
            'reservedCredits',job.reserved_credits,'replayed',provider.provider_job_id IS NOT NULL);
    ELSIF action = 'submitted' THEN
        provider_job_id := COALESCE(payload->>'providerJobId','');
        IF job_id_value IS NULL OR provider_job_id !~ '^[A-Za-z0-9._:-]{1,128}$' THEN RAISE EXCEPTION 'AI_REQUEST_INVALID'; END IF;
        SELECT * INTO job FROM private.ai_jobs WHERE job_id=job_id_value AND user_id=actor_id FOR UPDATE;
        IF NOT FOUND THEN RAISE EXCEPTION 'AI_JOB_NOT_FOUND'; END IF;
        IF job.status='Queued' THEN
            lease_value := gen_random_uuid();
            UPDATE private.ai_jobs SET status='Processing',attempt_count=attempt_count+1,
                processing_at=COALESCE(processing_at,clock_timestamp()),lease_token=lease_value,
                lease_expires_at=clock_timestamp()+interval '30 minutes',updated_at=clock_timestamp()
            WHERE job_id=job_id_value RETURNING * INTO job;
            UPDATE private.ai_provider_jobs SET provider_job_id=provider_job_id,updated_at=clock_timestamp()
            WHERE job_id=job_id_value;
        ELSIF job.status<>'Processing' THEN RAISE EXCEPTION 'AI_JOB_TRANSITION_INVALID'; END IF;
        RETURN jsonb_build_object('jobId',job.job_id,'status',job.status,'providerJobId',provider_job_id);
    ELSIF action = 'complete' THEN
        result_url := COALESCE(payload->>'resultUrl','');
        IF job_id_value IS NULL OR result_url !~ '^https://[^[:space:]]{1,2039}$' THEN RAISE EXCEPTION 'AI_RESULT_INVALID'; END IF;
        SELECT * INTO job FROM private.ai_jobs WHERE job_id=job_id_value AND user_id=actor_id FOR UPDATE;
        SELECT * INTO provider FROM private.ai_provider_jobs WHERE job_id=job_id_value FOR UPDATE;
        IF job.status='Completed' THEN RETURN jsonb_build_object('jobId',job.job_id,'status',job.status,'resultUrl',provider.result_url); END IF;
        IF job.status<>'Processing' OR provider.provider_job_id IS NULL THEN RAISE EXCEPTION 'AI_JOB_TRANSITION_INVALID'; END IF;
        SELECT * INTO credit_result FROM private.credit_capture(actor_id,job.reservation_id,job.reserved_credits,
            'capture:'||job.job_id::text,job.request_hash);
        UPDATE private.ai_provider_jobs SET result_url=result_url,updated_at=clock_timestamp() WHERE job_id=job.job_id;
        UPDATE private.ai_jobs SET status='Completed',final_credits=job.reserved_credits,
            capture_transaction_id=credit_result.transaction_id,output_reference='provider-result/'||job.job_id::text,
            provider_request_id=provider.provider_job_id,completed_at=clock_timestamp(),lease_token=NULL,
            lease_expires_at=NULL,updated_at=clock_timestamp() WHERE job_id=job.job_id RETURNING * INTO job;
        RETURN jsonb_build_object('jobId',job.job_id,'status',job.status,'resultUrl',result_url,'finalCredits',job.final_credits);
    ELSIF action = 'fail' THEN
        error_code := upper(COALESCE(payload->>'errorCode','AI_PROVIDER_FAILED'));
        IF job_id_value IS NULL OR error_code !~ '^[A-Z0-9_]{1,128}$' THEN RAISE EXCEPTION 'AI_REQUEST_INVALID'; END IF;
        SELECT * INTO job FROM private.ai_jobs WHERE job_id=job_id_value AND user_id=actor_id FOR UPDATE;
        IF NOT FOUND THEN RAISE EXCEPTION 'AI_JOB_NOT_FOUND'; END IF;
        IF job.status IN ('Queued','Processing') THEN
            SELECT * INTO credit_result FROM private.credit_release(actor_id,job.reservation_id,
                'fail:'||job.job_id::text,job.request_hash);
            UPDATE private.ai_jobs SET status='Failed',error_code=error_code,failed_at=clock_timestamp(),
                lease_token=NULL,lease_expires_at=NULL,updated_at=clock_timestamp()
            WHERE job_id=job.job_id RETURNING * INTO job;
        END IF;
        RETURN jsonb_build_object('jobId',job.job_id,'status',job.status,'errorCode',job.error_code);
    ELSIF action = 'status' THEN
        SELECT * INTO job FROM private.ai_jobs WHERE job_id=job_id_value AND user_id=actor_id;
        SELECT * INTO provider FROM private.ai_provider_jobs WHERE job_id=job_id_value AND user_id=actor_id;
        IF NOT FOUND OR job.job_id IS NULL THEN RAISE EXCEPTION 'AI_JOB_NOT_FOUND'; END IF;
        RETURN jsonb_build_object('jobId',job.job_id,'status',job.status,'providerJobId',provider.provider_job_id,
            'resultUrl',provider.result_url,'reservedCredits',job.reserved_credits,'finalCredits',job.final_credits,
            'createdAt',job.created_at,'errorCode',job.error_code);
    ELSIF action = 'history' THEN
        RETURN jsonb_build_object('jobs',COALESCE((SELECT jsonb_agg(jsonb_build_object(
            'jobId',j.job_id,'operation',j.operation,'status',j.status,'reservedCredits',j.reserved_credits,
            'finalCredits',j.final_credits,'createdAt',j.created_at,'canCancel',false) ORDER BY j.created_at DESC)
            FROM (SELECT * FROM private.ai_jobs WHERE user_id=actor_id ORDER BY created_at DESC LIMIT 100) j),'[]'::jsonb));
    ELSE
        RAISE EXCEPTION 'AI_ACTION_UNKNOWN';
    END IF;
END
$function$;

REVOKE ALL ON FUNCTION public.ai_image_api(text,jsonb) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.ai_image_api(text,jsonb) TO service_role;

COMMIT;
