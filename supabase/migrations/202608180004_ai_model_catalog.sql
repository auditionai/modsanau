BEGIN;

CREATE TABLE IF NOT EXISTS private.ai_model_catalog (
    model_id varchar(64) PRIMARY KEY CHECK (model_id ~ '^[a-z0-9._-]{1,64}$'),
    display_name varchar(160) NOT NULL CHECK (length(btrim(display_name)) BETWEEN 1 AND 160),
    credit_cost bigint NOT NULL DEFAULT 10 CHECK (credit_cost BETWEEN 1 AND 1000000),
    active boolean NOT NULL DEFAULT true,
    sort_order integer NOT NULL DEFAULT 0 CHECK (sort_order BETWEEN -100000 AND 100000),
    pricing_version integer NOT NULL DEFAULT 1 CHECK (pricing_version > 0),
    provider_metadata jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(provider_metadata) = 'object'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

ALTER TABLE private.ai_model_catalog ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.ai_model_catalog FORCE ROW LEVEL SECURITY;
REVOKE ALL ON private.ai_model_catalog FROM PUBLIC, anon, authenticated;
GRANT SELECT, INSERT, UPDATE ON private.ai_model_catalog TO service_role;

CREATE OR REPLACE FUNCTION public.ai_model_catalog_api(action text, payload jsonb DEFAULT '{}'::jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, private
AS $function$
DECLARE
    item jsonb;
    model_id_value text;
    display_name_value text;
    model_row private.ai_model_catalog%ROWTYPE;
    admin_id uuid := NULLIF(payload->>'adminUserId', '')::uuid;
    admin_row private.admin_users%ROWTYPE;
    cost_value bigint;
BEGIN
    IF NOT (current_setting('request.jwt.claim.role', true) = 'service_role'
        OR pg_has_role(current_user, 'service_role', 'MEMBER') OR current_user = 'postgres') THEN
        RAISE EXCEPTION 'AI_MODEL_SERVICE_ROLE_REQUIRED' USING ERRCODE = '42501';
    END IF;
    IF payload IS NULL OR jsonb_typeof(payload) <> 'object' THEN
        RAISE EXCEPTION 'AI_MODEL_PAYLOAD_INVALID' USING ERRCODE = 'P0001';
    END IF;

    IF action = 'sync' THEN
        IF jsonb_typeof(payload->'models') <> 'array' OR jsonb_array_length(payload->'models') > 500 THEN
            RAISE EXCEPTION 'AI_MODEL_CATALOG_INVALID' USING ERRCODE = 'P0001';
        END IF;
        FOR item IN SELECT value FROM jsonb_array_elements(payload->'models') LOOP
            model_id_value := lower(btrim(item->>'id'));
            display_name_value := left(btrim(COALESCE(item->>'name', model_id_value)), 160);
            IF model_id_value !~ '^[a-z0-9._-]{1,64}$' OR display_name_value = '' THEN CONTINUE; END IF;
            INSERT INTO private.ai_model_catalog(model_id, display_name, provider_metadata)
            VALUES (model_id_value, display_name_value, jsonb_build_object('provider', 'tst'))
            ON CONFLICT (model_id) DO UPDATE SET display_name = EXCLUDED.display_name,
                provider_metadata = private.ai_model_catalog.provider_metadata || EXCLUDED.provider_metadata,
                updated_at = clock_timestamp();
        END LOOP;
        RETURN COALESCE((SELECT jsonb_agg(jsonb_build_object(
            'id', model_id, 'name', display_name, 'creditCost', credit_cost,
            'active', active, 'pricingVersion', pricing_version, 'sortOrder', sort_order)
            ORDER BY sort_order, model_id) FROM private.ai_model_catalog), '[]'::jsonb);
    END IF;

    IF action IN ('public', 'admin_list', 'admin_save') THEN
        IF action LIKE 'admin_%' OR action = 'admin_save' THEN
            IF admin_id IS NULL THEN RAISE EXCEPTION 'ADMIN_FORBIDDEN' USING ERRCODE = '42501'; END IF;
            SELECT * INTO admin_row FROM private.admin_users WHERE admin_user_id = admin_id AND is_active;
            IF NOT FOUND THEN RAISE EXCEPTION 'ADMIN_FORBIDDEN' USING ERRCODE = '42501'; END IF;
        END IF;
        IF action = 'admin_save' THEN
            IF admin_row.role::text = 'auditor' OR admin_row.mfa_state::text <> 'MFA_VERIFIED'
                OR NOT COALESCE((payload->>'recentAuth')::boolean, false)
                OR length(btrim(payload->>'reason')) < 8 THEN
                RAISE EXCEPTION 'ADMIN_STRONG_AUTH_REQUIRED' USING ERRCODE = '42501';
            END IF;
            model_id_value := lower(btrim(payload->>'modelId'));
            display_name_value := left(btrim(payload->>'displayName'), 160);
            cost_value := NULLIF(payload->>'creditCost', '')::bigint;
            IF model_id_value !~ '^[a-z0-9._-]{1,64}$' OR display_name_value = ''
                OR cost_value IS NULL OR cost_value NOT BETWEEN 1 AND 1000000 THEN
                RAISE EXCEPTION 'AI_MODEL_CATALOG_INVALID' USING ERRCODE = 'P0001';
            END IF;
            INSERT INTO private.ai_model_catalog(model_id, display_name, credit_cost, active, sort_order, pricing_version)
            VALUES (model_id_value, display_name_value, cost_value,
                COALESCE((payload->>'active')::boolean, true), COALESCE((payload->>'sortOrder')::integer, 0), 1)
            ON CONFLICT (model_id) DO UPDATE SET display_name = EXCLUDED.display_name,
                credit_cost = EXCLUDED.credit_cost, active = EXCLUDED.active, sort_order = EXCLUDED.sort_order,
                pricing_version = private.ai_model_catalog.pricing_version + 1, updated_at = clock_timestamp()
            RETURNING * INTO model_row;
            INSERT INTO private.admin_audit_events(actor_admin_user_id, event_type, target_kind, target_id,
                correlation_id, details)
            VALUES (admin_id, 'AI_MODEL_PRICING_CHANGED', 'AI_MODEL', NULL,
                COALESCE(NULLIF(payload->>'correlationId', '')::uuid, gen_random_uuid()),
                jsonb_build_object('modelId', model_row.model_id, 'creditCost', model_row.credit_cost,
                    'active', model_row.active, 'reason', left(payload->>'reason', 500)));
            RETURN jsonb_build_object('modelId', model_row.model_id, 'pricingVersion', model_row.pricing_version);
        END IF;
        RETURN COALESCE((SELECT jsonb_agg(jsonb_build_object(
            'id', model_id, 'name', display_name, 'creditCost', credit_cost,
            'active', active, 'pricingVersion', pricing_version, 'sortOrder', sort_order)
            ORDER BY sort_order, model_id) FROM private.ai_model_catalog
            WHERE action = 'admin_list' OR active), '[]'::jsonb);
    END IF;
    RAISE EXCEPTION 'AI_MODEL_ACTION_UNKNOWN' USING ERRCODE = 'P0001';
END;
$function$;

REVOKE ALL ON FUNCTION public.ai_model_catalog_api(text, jsonb) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.ai_model_catalog_api(text, jsonb) TO service_role;

COMMIT;
