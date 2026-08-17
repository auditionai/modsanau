BEGIN;

-- Historical bootstrap retained in source because deployed environments already
-- have this migration version. Credential pooling is introduced by 014.
CREATE TABLE private.ai_provider_config (
    provider_id text PRIMARY KEY CHECK (provider_id = 'vertex_ai'),
    credentials_secret_id uuid NOT NULL,
    project_id text NOT NULL CHECK (project_id ~ '^[a-z][a-z0-9-]{4,61}[a-z0-9]$'),
    region text NOT NULL CHECK (region ~ '^[a-z]+-[a-z]+[0-9]$'),
    model_id text NOT NULL CHECK (model_id ~ '^[A-Za-z0-9._-]{3,128}$'),
    updated_by uuid NOT NULL REFERENCES auth.users(id),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

ALTER TABLE private.ai_provider_config ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.ai_provider_config FORCE ROW LEVEL SECURITY;
REVOKE ALL ON private.ai_provider_config FROM PUBLIC, anon, authenticated;
GRANT SELECT, INSERT, UPDATE ON private.ai_provider_config TO service_role;

CREATE OR REPLACE FUNCTION public.ai_vertex_credentials_api()
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, private, vault
AS $function$
DECLARE
    config private.ai_provider_config%ROWTYPE;
    credentials text;
BEGIN
    IF current_setting('request.jwt.claim.role', true) IS DISTINCT FROM 'service_role' THEN
        RAISE EXCEPTION 'AI_SERVICE_ROLE_REQUIRED' USING ERRCODE = '42501';
    END IF;
    SELECT * INTO config FROM private.ai_provider_config WHERE provider_id = 'vertex_ai';
    IF NOT FOUND THEN RAISE EXCEPTION 'VERTEX_NOT_CONFIGURED'; END IF;
    SELECT decrypted_secret INTO credentials FROM vault.decrypted_secrets WHERE id = config.credentials_secret_id;
    IF credentials IS NULL THEN RAISE EXCEPTION 'VERTEX_CREDENTIALS_UNAVAILABLE'; END IF;
    RETURN jsonb_build_object('credentialsJson', credentials, 'projectId', config.project_id,
        'region', config.region, 'modelId', config.model_id);
END
$function$;

REVOKE ALL ON FUNCTION public.ai_vertex_credentials_api() FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.ai_vertex_credentials_api() TO service_role;

COMMIT;
