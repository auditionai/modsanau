BEGIN;

CREATE OR REPLACE FUNCTION public.gpti2_pricing_admin_api(action text, payload jsonb DEFAULT '{}'::jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, public, private AS $function$
DECLARE
  admin_id uuid := NULLIF(payload->>'adminUserId', '')::uuid;
  admin_row private.admin_users%ROWTYPE;
  model_id_value text;
  model_row private.ai_model_catalog%ROWTYPE;
BEGIN
  IF NOT (current_setting('request.jwt.claim.role', true) = 'service_role'
    OR pg_has_role(current_user, 'service_role', 'MEMBER') OR current_user = 'postgres') THEN
    RAISE EXCEPTION 'AI_MODEL_SERVICE_ROLE_REQUIRED' USING ERRCODE = '42501';
  END IF;
  SELECT * INTO admin_row FROM private.admin_users WHERE admin_user_id = admin_id AND is_active;
  IF NOT FOUND OR admin_row.role::text = 'auditor' OR admin_row.mfa_state::text <> 'MFA_VERIFIED'
    OR NOT COALESCE((payload->>'recentAuth')::boolean, false) THEN
    IF action = 'save' THEN RAISE EXCEPTION 'ADMIN_STRONG_AUTH_REQUIRED' USING ERRCODE = '42501'; END IF;
  END IF;
  IF action = 'save' THEN
    model_id_value := lower(btrim(payload->>'modelId'));
    IF model_id_value NOT IN ('gpt-image-2', 'nano-banana-pro') THEN
      RAISE EXCEPTION 'AI_MODEL_NOT_ALLOWED' USING ERRCODE = 'P0001';
    END IF;
    UPDATE private.ai_model_catalog SET credit_cost = NULLIF(payload->>'creditCost', '')::bigint,
      active = COALESCE((payload->>'active')::boolean, true), pricing_version = pricing_version + 1,
      updated_at = clock_timestamp() WHERE model_id = model_id_value RETURNING * INTO model_row;
    IF NOT FOUND OR model_row.credit_cost IS NULL OR model_row.credit_cost NOT BETWEEN 1 AND 1000000 THEN
      RAISE EXCEPTION 'AI_MODEL_CATALOG_INVALID' USING ERRCODE = 'P0001';
    END IF;
    RETURN jsonb_build_object('modelId', model_row.model_id, 'pricingVersion', model_row.pricing_version);
  END IF;
  IF action <> 'list' THEN RAISE EXCEPTION 'AI_MODEL_ACTION_UNKNOWN' USING ERRCODE = 'P0001'; END IF;
  RETURN COALESCE((SELECT jsonb_agg(jsonb_build_object(
    'pricingId', model_id, 'modelId', model_id, 'modelName', display_name,
    'settings', jsonb_build_object('pricingMode','gpti2','baseImageVnd',50,
      'promptFreeTokens',1500,'promptSurchargePer1kVnd',29,'quantity','1-4',
      'quality',jsonb_build_array('low','medium','high'),'sizeCount',24),
    'gpti2Pricing', jsonb_build_object('baseImageVnd',50,'promptFreeTokens',1500,
      'promptSurchargePer1kVnd',29,'quantityMin',1,'quantityMax',4),
    'tstCost', NULL, 'creditCost', credit_cost, 'active', active,
    'pricingVersion', pricing_version) ORDER BY sort_order, model_id)
    FROM private.ai_model_catalog WHERE model_id IN ('gpt-image-2','nano-banana-pro')), '[]'::jsonb);
END;
$function$;

REVOKE ALL ON FUNCTION public.gpti2_pricing_admin_api(text, jsonb) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.gpti2_pricing_admin_api(text, jsonb) TO service_role;
COMMIT;
