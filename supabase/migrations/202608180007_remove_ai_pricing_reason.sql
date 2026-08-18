BEGIN;

CREATE OR REPLACE FUNCTION public.ai_model_catalog_api(action text, payload jsonb DEFAULT '{}'::jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, public, private AS $function$
DECLARE
    item jsonb;
    price_item jsonb;
    model_id_value text;
    display_name_value text;
    admin_id uuid := NULLIF(payload->>'adminUserId', '')::uuid;
    admin_row private.admin_users%ROWTYPE;
    cost_value bigint;
    provider_cost bigint;
    config_key_value text;
    settings_value jsonb;
    price_row private.ai_model_setting_prices%ROWTYPE;
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
            VALUES (model_id_value, display_name_value, jsonb_build_object('provider', 'tst', 'type', lower(COALESCE(item->>'type', ''))))
            ON CONFLICT (model_id) DO UPDATE SET display_name = EXCLUDED.display_name,
                provider_metadata = private.ai_model_catalog.provider_metadata || EXCLUDED.provider_metadata,
                updated_at = clock_timestamp();
            IF jsonb_typeof(item->'pricing') = 'array' THEN
                FOR price_item IN SELECT value FROM jsonb_array_elements(item->'pricing') LOOP
                    config_key_value := md5(price_item::text);
                    settings_value := price_item - ARRAY['credits', 'cost', 'price', 'key', 'config_key'];
                    provider_cost := CASE WHEN COALESCE(price_item->>'credits', price_item->>'cost', price_item->>'price') ~ '^[0-9]+$'
                        THEN (COALESCE(price_item->>'credits', price_item->>'cost', price_item->>'price'))::bigint ELSE NULL END;
                    INSERT INTO private.ai_model_setting_prices(model_id, config_key, settings, tst_cost)
                    VALUES (model_id_value, config_key_value, settings_value, provider_cost)
                    ON CONFLICT (model_id, config_key) DO UPDATE SET settings = EXCLUDED.settings,
                        tst_cost = EXCLUDED.tst_cost, updated_at = clock_timestamp();
                END LOOP;
            END IF;
        END LOOP;
        RETURN COALESCE((SELECT jsonb_agg(jsonb_build_object('id', model_id, 'name', display_name, 'creditCost', credit_cost,
            'active', active, 'pricingVersion', pricing_version, 'sortOrder', sort_order) ORDER BY sort_order, model_id)
            FROM private.ai_model_catalog WHERE provider_metadata->>'type' LIKE '%image%'), '[]'::jsonb);
    END IF;
    IF action = 'quote' THEN
        model_id_value := lower(btrim(payload->>'modelId'));
        RETURN COALESCE((SELECT jsonb_build_object('creditCost', credit_cost, 'pricingVersion', pricing_version, 'pricingId', pricing_id)
            FROM private.ai_model_setting_prices WHERE model_id = model_id_value AND active
              AND settings @> COALESCE(payload->'settings', '{}'::jsonb)
            ORDER BY jsonb_object_length(settings) DESC, credit_cost ASC LIMIT 1),
            (SELECT jsonb_build_object('creditCost', credit_cost, 'pricingVersion', pricing_version, 'pricingId', pricing_id)
             FROM private.ai_model_setting_prices WHERE model_id = model_id_value AND active AND jsonb_object_length(settings) = 0
             ORDER BY credit_cost ASC LIMIT 1));
    END IF;
    IF action IN ('public', 'admin_list', 'admin_save') THEN
        IF action LIKE 'admin_%' OR action = 'admin_save' THEN
            IF admin_id IS NULL THEN RAISE EXCEPTION 'ADMIN_FORBIDDEN' USING ERRCODE = '42501'; END IF;
            SELECT * INTO admin_row FROM private.admin_users WHERE admin_user_id = admin_id AND is_active;
            IF NOT FOUND THEN RAISE EXCEPTION 'ADMIN_FORBIDDEN' USING ERRCODE = '42501'; END IF;
        END IF;
        IF action = 'admin_save' THEN
            IF admin_row.role::text = 'auditor' OR admin_row.mfa_state::text <> 'MFA_VERIFIED'
                OR NOT COALESCE((payload->>'recentAuth')::boolean, false) THEN
                RAISE EXCEPTION 'ADMIN_STRONG_AUTH_REQUIRED' USING ERRCODE = '42501';
            END IF;
            cost_value := NULLIF(payload->>'creditCost', '')::bigint;
            IF NULLIF(payload->>'pricingId', '')::uuid IS NULL OR cost_value IS NULL OR cost_value NOT BETWEEN 1 AND 1000000 THEN
                RAISE EXCEPTION 'AI_MODEL_CATALOG_INVALID' USING ERRCODE = 'P0001';
            END IF;
            UPDATE private.ai_model_setting_prices SET credit_cost = cost_value,
                active = COALESCE((payload->>'active')::boolean, true), pricing_version = pricing_version + 1,
                updated_at = clock_timestamp() WHERE pricing_id = (payload->>'pricingId')::uuid RETURNING * INTO price_row;
            IF NOT FOUND THEN RAISE EXCEPTION 'AI_MODEL_PRICING_NOT_FOUND' USING ERRCODE = 'P0001'; END IF;
            INSERT INTO private.admin_audit_events(actor_admin_user_id, event_type, target_kind, target_id, correlation_id, details)
            VALUES (admin_id, 'AI_MODEL_SETTING_PRICING_CHANGED', 'AI_MODEL_PRICING', price_row.pricing_id,
                COALESCE(NULLIF(payload->>'correlationId', '')::uuid, gen_random_uuid()),
                jsonb_build_object('modelId', price_row.model_id, 'creditCost', price_row.credit_cost, 'active', price_row.active));
            RETURN jsonb_build_object('pricingId', price_row.pricing_id, 'pricingVersion', price_row.pricing_version);
        END IF;
        RETURN COALESCE((SELECT jsonb_agg(jsonb_build_object('pricingId', p.pricing_id, 'modelId', p.model_id,
            'modelName', m.display_name, 'settings', p.settings, 'tstCost', p.tst_cost, 'creditCost', p.credit_cost,
            'active', p.active, 'pricingVersion', p.pricing_version) ORDER BY m.display_name, p.model_id, p.settings::text)
            FROM private.ai_model_setting_prices p JOIN private.ai_model_catalog m ON m.model_id = p.model_id
            WHERE m.provider_metadata->>'type' LIKE '%image%' AND (action = 'admin_list' OR p.active)), '[]'::jsonb);
    END IF;
    RAISE EXCEPTION 'AI_MODEL_ACTION_UNKNOWN' USING ERRCODE = 'P0001';
END;
$function$;

COMMIT;
