BEGIN;

CREATE OR REPLACE FUNCTION public.gpti2_internal_quote(model_id_value text, requested_settings jsonb DEFAULT '{}'::jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, public, private AS $function$
DECLARE
BEGIN
  IF model_id_value NOT IN ('gpt-image-2', 'nano-banana-pro') THEN RETURN NULL; END IF;
  RETURN (
    SELECT jsonb_build_object('creditCost', p.credit_cost, 'pricingVersion', p.pricing_version, 'pricingId', p.pricing_id)
    FROM private.ai_model_setting_prices p
    WHERE p.model_id = model_id_value AND p.active
      AND p.settings @> requested_settings AND requested_settings @> p.settings
      AND p.settings ? 'size' AND p.settings ? 'quality'
    ORDER BY p.pricing_version DESC, p.credit_cost ASC
    LIMIT 1
  );
END;
$function$;

REVOKE ALL ON FUNCTION public.gpti2_internal_quote(text, jsonb) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.gpti2_internal_quote(text, jsonb) TO service_role;

COMMIT;
