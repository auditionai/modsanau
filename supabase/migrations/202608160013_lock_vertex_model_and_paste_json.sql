BEGIN;

UPDATE private.ai_provider_config
SET model_id = 'gemini-3.6-flash'
WHERE provider_id = 'vertex_ai'
  AND model_id NOT IN ('gemini-3.6-flash', 'gemini-3.1-flash');

ALTER TABLE private.ai_provider_config
    ADD CONSTRAINT ai_provider_config_model_generation_check
    CHECK (model_id IN ('gemini-3.6-flash', 'gemini-3.1-flash'));

CREATE OR REPLACE FUNCTION public.ai_provider_admin_api(action text, payload jsonb DEFAULT '{}'::jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, private, vault
AS $function$
DECLARE
    actor_id uuid := auth.uid();
    actor_role text;
    credentials jsonb;
    credentials_text text;
    secret_id uuid;
    project_value text;
    correlation_id uuid := NULLIF(payload->>'correlationId', '')::uuid;
BEGIN
    IF actor_id IS NULL THEN RAISE EXCEPTION 'ADMIN_AUTH_REQUIRED' USING ERRCODE = '42501'; END IF;
    SELECT role::text INTO actor_role FROM private.admin_users WHERE admin_user_id = actor_id AND is_active;
    IF actor_role <> 'owner' THEN RAISE EXCEPTION 'ADMIN_OWNER_REQUIRED' USING ERRCODE = '42501'; END IF;

    IF action = 'status' THEN
        RETURN COALESCE((SELECT jsonb_build_object('configured', true, 'projectId', project_id,
            'region', region, 'modelId', model_id, 'updatedAt', updated_at)
            FROM private.ai_provider_config WHERE provider_id = 'vertex_ai'), jsonb_build_object('configured', false));
    END IF;
    IF action <> 'save_vertex_credentials' THEN RAISE EXCEPTION 'ADMIN_ACTION_UNKNOWN'; END IF;
    credentials_text := payload->>'credentialsJson';
    IF credentials_text IS NULL OR length(credentials_text) NOT BETWEEN 200 AND 20000 THEN RAISE EXCEPTION 'VERTEX_CREDENTIALS_INVALID'; END IF;
    BEGIN credentials := credentials_text::jsonb; EXCEPTION WHEN others THEN RAISE EXCEPTION 'VERTEX_CREDENTIALS_INVALID'; END;
    project_value := credentials->>'project_id';
    IF credentials->>'type' <> 'service_account' OR project_value !~ '^[a-z][a-z0-9-]{4,61}[a-z0-9]$'
       OR length(COALESCE(credentials->>'client_email','')) NOT BETWEEN 8 AND 256
       OR left(COALESCE(credentials->>'private_key',''), 28) <> '-----BEGIN PRIVATE KEY-----' THEN
        RAISE EXCEPTION 'VERTEX_CREDENTIALS_INVALID';
    END IF;
    SELECT vault.create_secret(credentials_text, 'vertex-ai-' || gen_random_uuid()::text,
        'Vertex AI service account credential') INTO secret_id;
    INSERT INTO private.ai_provider_config(provider_id, credentials_secret_id, project_id, region, model_id, updated_by)
    VALUES ('vertex_ai', secret_id, project_value, 'us-central1', 'gemini-3.6-flash', actor_id)
    ON CONFLICT(provider_id) DO UPDATE SET credentials_secret_id = excluded.credentials_secret_id,
        project_id = excluded.project_id, region = excluded.region, model_id = excluded.model_id,
        updated_by = excluded.updated_by, updated_at = clock_timestamp();
    PERFORM private.admin_audit_record(actor_id, 'VERTEX_AI_CREDENTIAL_ROTATED', 'AI_PROVIDER', secret_id,
        COALESCE(correlation_id, gen_random_uuid()), jsonb_build_object('projectId', project_value));
    RETURN jsonb_build_object('code', 'VERTEX_AI_CREDENTIAL_SAVED', 'configured', true,
        'projectId', project_value, 'region', 'us-central1', 'modelId', 'gemini-3.6-flash');
END
$function$;

REVOKE ALL ON FUNCTION public.ai_provider_admin_api(text, jsonb) FROM PUBLIC, anon;
GRANT EXECUTE ON FUNCTION public.ai_provider_admin_api(text, jsonb) TO authenticated;

COMMIT;
