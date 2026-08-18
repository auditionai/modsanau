BEGIN;
CREATE OR REPLACE FUNCTION public.gpti2_pricing_admin_api(action text, payload jsonb DEFAULT '{}'::jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, private AS $function$
DECLARE
  admin_id uuid := NULLIF(payload->>'adminUserId', '')::uuid;
  admin_row private.admin_users%ROWTYPE;
  model_id_value text;
  size_value text;
  quality_value text;
  cost_value bigint;
  matrix jsonb := '[
    {"size":"1024x1024","low":50,"medium":340,"high":1340},{"size":"1536x1536","low":60,"medium":480,"high":1910},{"size":"2048x2048","low":80,"medium":680,"high":2720},
    {"size":"1280x720","low":50,"medium":340,"high":730},{"size":"2560x1440","low":50,"medium":350,"high":1400},{"size":"3840x2160","low":80,"medium":640,"high":2540},
    {"size":"720x1280","low":50,"medium":190,"high":730},{"size":"1440x2560","low":50,"medium":350,"high":1400},{"size":"2160x3840","low":80,"medium":640,"high":2540},
    {"size":"1024x768","low":50,"medium":230,"high":920},{"size":"2048x1536","low":50,"medium":430,"high":1690},{"size":"3200x2400","low":90,"medium":800,"high":3180},
    {"size":"768x1024","low":50,"medium":230,"high":920},{"size":"1536x2048","low":50,"medium":430,"high":1690},{"size":"2400x3200","low":90,"medium":800,"high":3180},
    {"size":"1536x1024","low":50,"medium":280,"high":1090},{"size":"2400x1600","low":50,"medium":440,"high":1760},{"size":"3360x2240","low":90,"medium":720,"high":2880},
    {"size":"1024x1536","low":50,"medium":280,"high":1090},{"size":"1600x2400","low":50,"medium":440,"high":1760},{"size":"2240x3360","low":90,"medium":720,"high":2880},
    {"size":"1280x544","low":50,"medium":130,"high":500},{"size":"2560x1088","low":50,"medium":230,"high":920},{"size":"3840x1632","low":50,"medium":400,"high":1590}
  ]'::jsonb;
BEGIN
  IF action = 'save' THEN
    SELECT * INTO admin_row FROM private.admin_users WHERE admin_user_id = admin_id AND is_active;
    IF NOT FOUND OR admin_row.role::text = 'auditor' OR admin_row.mfa_state::text <> 'MFA_VERIFIED'
      OR NOT COALESCE((payload->>'recentAuth')::boolean, false) THEN RAISE EXCEPTION 'ADMIN_STRONG_AUTH_REQUIRED' USING ERRCODE='42501'; END IF;
    model_id_value := lower(payload->>'modelId'); size_value := payload->>'size'; quality_value := lower(payload->>'quality');
    cost_value := NULLIF(payload->>'creditCost','')::bigint;
    IF model_id_value NOT IN ('gpt-image-2','nano-banana-pro') OR size_value IS NULL OR quality_value NOT IN ('low','medium','high') OR cost_value IS NULL OR cost_value NOT BETWEEN 1 AND 1000000 THEN RAISE EXCEPTION 'AI_MODEL_CATALOG_INVALID' USING ERRCODE='P0001'; END IF;
    INSERT INTO private.ai_model_setting_prices(model_id, config_key, settings, tst_cost, credit_cost)
      VALUES (model_id_value, size_value || ':' || quality_value, jsonb_build_object('size',size_value,'quality',quality_value), NULL, cost_value)
      ON CONFLICT (model_id, config_key) DO UPDATE SET credit_cost=EXCLUDED.credit_cost, active=true, pricing_version=private.ai_model_setting_prices.pricing_version+1, updated_at=clock_timestamp();
    RETURN jsonb_build_object('modelId',model_id_value,'size',size_value,'quality',quality_value);
  END IF;
  IF action <> 'list' THEN RAISE EXCEPTION 'AI_MODEL_ACTION_UNKNOWN' USING ERRCODE='P0001'; END IF;
  RETURN COALESCE((SELECT jsonb_agg(jsonb_build_object('pricingId',m.model_id||':'||(x.item->>'size')||':'||q.quality,'modelId',m.model_id,'modelName',m.display_name,'settings',jsonb_build_object('size',x.item->>'size','quality',q.quality),'gpti2PriceVnd',(x.item->>q.quality)::bigint,'promptFreeTokens',1500,'promptSurchargePer1kVnd',29,'creditCost',COALESCE(p.credit_cost,10),'active',COALESCE(p.active,true),'pricingVersion',COALESCE(p.pricing_version,1)) ORDER BY m.sort_order,m.model_id,x.item->>'size',q.quality) FROM private.ai_model_catalog m CROSS JOIN LATERAL jsonb_array_elements(matrix) x(item) CROSS JOIN LATERAL (VALUES ('low'),('medium'),('high')) q(quality) LEFT JOIN private.ai_model_setting_prices p ON p.model_id=m.model_id AND p.config_key=(x.item->>'size')||':'||q.quality WHERE m.model_id IN ('gpt-image-2','nano-banana-pro')), '[]'::jsonb);
END;
$function$;
REVOKE ALL ON FUNCTION public.gpti2_pricing_admin_api(text,jsonb) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.gpti2_pricing_admin_api(text,jsonb) TO service_role;
COMMIT;
