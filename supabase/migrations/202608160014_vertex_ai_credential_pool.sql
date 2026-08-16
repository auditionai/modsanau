BEGIN;

-- Self-contained upgrade: older deployments used a singleton config row while
-- some source branches did not retain those migrations. Keep that row only as
-- a one-time migration source; all runtime selection moves to this pool.
CREATE TABLE IF NOT EXISTS private.ai_provider_config (
    provider_id text PRIMARY KEY CHECK (provider_id = 'vertex_ai'),
    credentials_secret_id uuid NOT NULL,
    project_id text NOT NULL,
    region text NOT NULL,
    model_id text NOT NULL,
    updated_by uuid NOT NULL REFERENCES auth.users(id),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE private.vertex_ai_credentials (
    credential_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    credentials_secret_id uuid NOT NULL UNIQUE,
    project_id text NOT NULL CHECK (project_id ~ '^[a-z][a-z0-9-]{4,61}[a-z0-9]$'),
    region text NOT NULL DEFAULT 'us-central1' CHECK (region = 'us-central1'),
    model_id text NOT NULL DEFAULT 'gemini-3.6-flash'
        CHECK (model_id IN ('gemini-3.6-flash', 'gemini-3.1-flash')),
    enabled boolean NOT NULL DEFAULT true,
    retired_at timestamptz,
    cooldown_until timestamptz,
    failure_streak integer NOT NULL DEFAULT 0 CHECK (failure_streak BETWEEN 0 AND 32),
    last_selected_at timestamptz,
    last_success_at timestamptz,
    last_failure_at timestamptz,
    last_failure_code text CHECK (last_failure_code IS NULL OR last_failure_code ~ '^[A-Z0-9_]{3,64}$'),
    created_by uuid NOT NULL REFERENCES auth.users(id),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK (retired_at IS NULL OR enabled = false)
);

CREATE INDEX vertex_ai_credentials_select_idx
    ON private.vertex_ai_credentials(enabled, retired_at, cooldown_until, last_selected_at, credential_id);

ALTER TABLE private.vertex_ai_credentials ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.vertex_ai_credentials FORCE ROW LEVEL SECURITY;
REVOKE ALL ON private.vertex_ai_credentials FROM PUBLIC, anon, authenticated;
GRANT SELECT, INSERT, UPDATE ON private.vertex_ai_credentials TO service_role;

-- Preserve a configured singleton as the initial member of the pool.
INSERT INTO private.vertex_ai_credentials (
    credentials_secret_id, project_id, region, model_id, enabled, created_by, created_at, updated_at)
SELECT credentials_secret_id, project_id, 'us-central1', 'gemini-3.6-flash', true, updated_by, updated_at, updated_at
FROM private.ai_provider_config
WHERE provider_id = 'vertex_ai'
ON CONFLICT (credentials_secret_id) DO NOTHING;

CREATE OR REPLACE FUNCTION private.vertex_ai_require_service_role()
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $function$
BEGIN
    IF current_setting('request.jwt.claim.role', true) IS DISTINCT FROM 'service_role' THEN
        RAISE EXCEPTION 'AI_SERVICE_ROLE_REQUIRED' USING ERRCODE = '42501';
    END IF;
END
$function$;

CREATE OR REPLACE FUNCTION public.ai_vertex_credential_acquire_api()
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, private, vault
AS $function$
DECLARE
    selected private.vertex_ai_credentials%ROWTYPE;
    credentials_text text;
BEGIN
    PERFORM private.vertex_ai_require_service_role();

    SELECT * INTO selected
    FROM private.vertex_ai_credentials
    WHERE enabled
      AND retired_at IS NULL
      AND (cooldown_until IS NULL OR cooldown_until <= clock_timestamp())
    ORDER BY last_selected_at NULLS FIRST, failure_streak, credential_id
    FOR UPDATE SKIP LOCKED
    LIMIT 1;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'VERTEX_POOL_UNAVAILABLE' USING ERRCODE = 'P0001';
    END IF;

    SELECT decrypted_secret INTO credentials_text
    FROM vault.decrypted_secrets
    WHERE id = selected.credentials_secret_id;
    IF credentials_text IS NULL THEN
        UPDATE private.vertex_ai_credentials
        SET enabled = false, last_failure_at = clock_timestamp(),
            last_failure_code = 'SECRET_UNAVAILABLE', updated_at = clock_timestamp()
        WHERE credential_id = selected.credential_id;
        RAISE EXCEPTION 'VERTEX_CREDENTIALS_UNAVAILABLE' USING ERRCODE = 'P0001';
    END IF;

    UPDATE private.vertex_ai_credentials
    SET last_selected_at = clock_timestamp(), updated_at = clock_timestamp()
    WHERE credential_id = selected.credential_id;

    RETURN jsonb_build_object(
        'credentialId', selected.credential_id,
        'credentialsJson', credentials_text,
        'projectId', selected.project_id,
        'region', selected.region,
        'modelId', selected.model_id);
END
$function$;

CREATE OR REPLACE FUNCTION public.ai_vertex_credential_report_api(
    input_credential_id uuid,
    input_outcome text,
    input_error_code text DEFAULT NULL)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, private
AS $function$
DECLARE
    now_at timestamptz := clock_timestamp();
BEGIN
    PERFORM private.vertex_ai_require_service_role();
    IF input_credential_id IS NULL OR input_outcome NOT IN ('success', 'quota', 'transient', 'auth') THEN
        RAISE EXCEPTION 'VERTEX_REPORT_INVALID' USING ERRCODE = 'P0001';
    END IF;

    IF input_outcome = 'success' THEN
        UPDATE private.vertex_ai_credentials
        SET failure_streak = 0, cooldown_until = NULL, last_success_at = now_at,
            last_failure_at = NULL, last_failure_code = NULL, updated_at = now_at
        WHERE credential_id = input_credential_id;
    ELSIF input_outcome = 'auth' THEN
        UPDATE private.vertex_ai_credentials
        SET enabled = false, cooldown_until = NULL, last_failure_at = now_at,
            last_failure_code = COALESCE(input_error_code, 'AUTH_FAILED'), updated_at = now_at
        WHERE credential_id = input_credential_id;
    ELSE
        UPDATE private.vertex_ai_credentials
        SET failure_streak = LEAST(32, failure_streak + 1),
            cooldown_until = now_at + make_interval(secs => CASE
                WHEN input_outcome = 'quota' THEN LEAST(3600, 60 * (2 ^ LEAST(failure_streak, 5))::integer)
                ELSE LEAST(300, 15 * (2 ^ LEAST(failure_streak, 4))::integer)
            END),
            last_failure_at = now_at,
            last_failure_code = COALESCE(input_error_code, CASE WHEN input_outcome = 'quota' THEN 'QUOTA' ELSE 'TRANSIENT' END),
            updated_at = now_at
        WHERE credential_id = input_credential_id;
    END IF;

    IF NOT FOUND THEN RAISE EXCEPTION 'VERTEX_CREDENTIAL_NOT_FOUND' USING ERRCODE = 'P0001'; END IF;
END
$function$;

CREATE OR REPLACE FUNCTION public.ai_provider_admin_api(action text, payload jsonb DEFAULT '{}'::jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, private, vault, extensions
AS $function$
DECLARE
    actor_id uuid := auth.uid();
    actor_role text;
    credentials jsonb;
    credentials_text text;
    secret_id uuid;
    target_credential_id uuid := NULLIF(payload->>'credentialId', '')::uuid;
    project_value text;
    enabled_value boolean;
    correlation_id uuid := NULLIF(payload->>'correlationId', '')::uuid;
    active_count integer;
BEGIN
    IF actor_id IS NULL THEN RAISE EXCEPTION 'ADMIN_AUTH_REQUIRED' USING ERRCODE = '42501'; END IF;
    SELECT role::text INTO actor_role FROM private.admin_users WHERE admin_user_id = actor_id AND is_active;
    IF actor_role <> 'owner' THEN RAISE EXCEPTION 'ADMIN_OWNER_REQUIRED' USING ERRCODE = '42501'; END IF;

    IF action = 'status' THEN
        RETURN jsonb_build_object(
            'configured', EXISTS (SELECT 1 FROM private.vertex_ai_credentials WHERE enabled AND retired_at IS NULL),
            'modelPolicy', 'Gemini 3.6 -> Gemini 3.1',
            'credentials', COALESCE((SELECT jsonb_agg(jsonb_build_object(
                'credentialId', credential_id, 'projectId', project_id, 'enabled', enabled,
                'retiredAt', retired_at, 'cooldownUntil', cooldown_until, 'failureStreak', failure_streak,
                'lastSelectedAt', last_selected_at, 'lastSuccessAt', last_success_at,
                'lastFailureAt', last_failure_at, 'lastFailureCode', last_failure_code,
                'createdAt', created_at) ORDER BY created_at DESC)
                FROM private.vertex_ai_credentials), '[]'::jsonb));
    END IF;

    IF action = 'add_vertex_credential' THEN
        credentials_text := payload->>'credentialsJson';
        IF credentials_text IS NULL OR length(credentials_text) NOT BETWEEN 200 AND 20000 THEN RAISE EXCEPTION 'VERTEX_CREDENTIALS_INVALID'; END IF;
        BEGIN credentials := credentials_text::jsonb; EXCEPTION WHEN others THEN RAISE EXCEPTION 'VERTEX_CREDENTIALS_INVALID'; END;
        project_value := credentials->>'project_id';
        IF credentials->>'type' <> 'service_account'
           OR project_value !~ '^[a-z][a-z0-9-]{4,61}[a-z0-9]$'
           OR length(COALESCE(credentials->>'client_email','')) NOT BETWEEN 8 AND 256
           OR left(COALESCE(credentials->>'private_key',''), 28) <> '-----BEGIN PRIVATE KEY-----' THEN
            RAISE EXCEPTION 'VERTEX_CREDENTIALS_INVALID';
        END IF;
        SELECT vault.create_secret(credentials_text, 'vertex-ai-' || gen_random_uuid()::text,
            'Vertex AI pooled service account credential') INTO secret_id;
        INSERT INTO private.vertex_ai_credentials(credentials_secret_id, project_id, created_by)
        VALUES (secret_id, project_value, actor_id) RETURNING credential_id INTO target_credential_id;
        PERFORM private.admin_audit_record(actor_id, 'VERTEX_AI_CREDENTIAL_ADDED', 'AI_PROVIDER', target_credential_id,
            COALESCE(correlation_id, gen_random_uuid()), jsonb_build_object('projectId', project_value));
        RETURN jsonb_build_object('code', 'VERTEX_AI_CREDENTIAL_ADDED', 'credentialId', target_credential_id);
    END IF;

    IF action IN ('set_vertex_credential_enabled', 'retire_vertex_credential') THEN
        IF target_credential_id IS NULL THEN RAISE EXCEPTION 'VERTEX_CREDENTIAL_NOT_FOUND'; END IF;
        SELECT count(*) INTO active_count FROM private.vertex_ai_credentials WHERE enabled AND retired_at IS NULL;
        enabled_value := CASE WHEN action = 'retire_vertex_credential' THEN false ELSE COALESCE((payload->>'enabled')::boolean, false) END;
        IF NOT enabled_value AND active_count <= 1
           AND EXISTS (SELECT 1 FROM private.vertex_ai_credentials AS item WHERE item.credential_id = target_credential_id AND item.enabled AND item.retired_at IS NULL) THEN
            RAISE EXCEPTION 'VERTEX_LAST_CREDENTIAL_REQUIRED';
        END IF;
        UPDATE private.vertex_ai_credentials
        SET enabled = enabled_value,
            retired_at = CASE WHEN action = 'retire_vertex_credential' THEN clock_timestamp() ELSE retired_at END,
            cooldown_until = CASE WHEN enabled_value THEN NULL ELSE cooldown_until END,
            updated_at = clock_timestamp()
        WHERE credential_id = target_credential_id
        RETURNING credential_id INTO target_credential_id;
        IF NOT FOUND THEN RAISE EXCEPTION 'VERTEX_CREDENTIAL_NOT_FOUND'; END IF;
        PERFORM private.admin_audit_record(actor_id,
            CASE WHEN action = 'retire_vertex_credential' THEN 'VERTEX_AI_CREDENTIAL_RETIRED' ELSE 'VERTEX_AI_CREDENTIAL_STATE_CHANGED' END,
            'AI_PROVIDER', target_credential_id, COALESCE(correlation_id, gen_random_uuid()),
            jsonb_build_object('enabled', enabled_value));
        RETURN jsonb_build_object('code', 'VERTEX_AI_CREDENTIAL_UPDATED', 'credentialId', target_credential_id);
    END IF;

    IF action = 'reset_vertex_credential_cooldown' THEN
        UPDATE private.vertex_ai_credentials SET cooldown_until = NULL, failure_streak = 0, updated_at = clock_timestamp()
        WHERE credential_id = target_credential_id AND enabled AND retired_at IS NULL;
        IF NOT FOUND THEN RAISE EXCEPTION 'VERTEX_CREDENTIAL_NOT_FOUND'; END IF;
        PERFORM private.admin_audit_record(actor_id, 'VERTEX_AI_CREDENTIAL_RESET', 'AI_PROVIDER', target_credential_id,
            COALESCE(correlation_id, gen_random_uuid()), '{}'::jsonb);
        RETURN jsonb_build_object('code', 'VERTEX_AI_CREDENTIAL_RESET', 'credentialId', target_credential_id);
    END IF;

    RAISE EXCEPTION 'ADMIN_ACTION_UNKNOWN';
END
$function$;

REVOKE ALL ON FUNCTION private.vertex_ai_require_service_role() FROM PUBLIC, anon, authenticated;
REVOKE ALL ON FUNCTION public.ai_vertex_credential_acquire_api() FROM PUBLIC, anon, authenticated;
REVOKE ALL ON FUNCTION public.ai_vertex_credential_report_api(uuid, text, text) FROM PUBLIC, anon, authenticated;
REVOKE ALL ON FUNCTION public.ai_provider_admin_api(text, jsonb) FROM PUBLIC, anon;
GRANT EXECUTE ON FUNCTION public.ai_vertex_credential_acquire_api() TO service_role;
GRANT EXECUTE ON FUNCTION public.ai_vertex_credential_report_api(uuid, text, text) TO service_role;
GRANT EXECUTE ON FUNCTION public.ai_provider_admin_api(text, jsonb) TO authenticated;

COMMIT;
