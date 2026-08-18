BEGIN;

CREATE OR REPLACE FUNCTION public.ai_model_catalog_api(action text, payload jsonb DEFAULT '{}'::jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, public, private AS $function$
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
            VALUES (model_id_value, display_name_value, jsonb_build_object(
                'provider', 'tst', 'type', lower(COALESCE(item->>'type', '')),
                'pricing', COALESCE(item->'pricing', '[]'::jsonb)))
            ON CONFLICT (model_id) DO UPDATE SET display_name = EXCLUDED.display_name,
                provider_metadata = private.ai_model_catalog.provider_metadata || EXCLUDED.provider_metadata,
                updated_at = clock_timestamp();
        END LOOP;
        RETURN COALESCE((SELECT jsonb_agg(jsonb_build_object(
            'id', model_id, 'name', display_name, 'creditCost', credit_cost,
            'tstPricing', provider_metadata->'pricing', 'active', active,
            'pricingVersion', pricing_version, 'sortOrder', sort_order)
            ORDER BY sort_order, model_id) FROM private.ai_model_catalog
            WHERE provider_metadata->>'type' LIKE '%image%'), '[]'::jsonb);
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
            UPDATE private.ai_model_catalog SET display_name = display_name_value,
                credit_cost = cost_value, active = COALESCE((payload->>'active')::boolean, true),
                sort_order = COALESCE((payload->>'sortOrder')::integer, 0),
                pricing_version = pricing_version + 1, updated_at = clock_timestamp()
                WHERE model_id = model_id_value RETURNING * INTO model_row;
            IF NOT FOUND THEN RAISE EXCEPTION 'AI_MODEL_NOT_FOUND' USING ERRCODE = 'P0001'; END IF;
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
            'tstPricing', provider_metadata->'pricing', 'active', active,
            'pricingVersion', pricing_version, 'sortOrder', sort_order)
            ORDER BY sort_order, model_id) FROM private.ai_model_catalog
            WHERE provider_metadata->>'type' LIKE '%image%' AND (action = 'admin_list' OR active)), '[]'::jsonb);
    END IF;
    RAISE EXCEPTION 'AI_MODEL_ACTION_UNKNOWN' USING ERRCODE = 'P0001';
END;
$function$;

COMMIT;
