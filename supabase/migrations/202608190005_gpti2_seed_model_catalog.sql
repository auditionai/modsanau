BEGIN;

UPDATE private.ai_model_catalog
SET model_id = 'gpt-image-2', display_name = 'GPT Image 2', active = true,
    provider_metadata = jsonb_build_object('provider','gpti2','type','image'), updated_at = clock_timestamp()
WHERE model_id = 'image-gpt-2'
  AND NOT EXISTS (SELECT 1 FROM private.ai_model_catalog WHERE model_id = 'gpt-image-2');

INSERT INTO private.ai_model_catalog(model_id, display_name, credit_cost, active, sort_order, provider_metadata)
VALUES ('gpt-image-2', 'GPT Image 2', 10, true, 10, jsonb_build_object('provider','gpti2','type','image'))
ON CONFLICT (model_id) DO UPDATE SET display_name = EXCLUDED.display_name,
  provider_metadata = EXCLUDED.provider_metadata, active = true, updated_at = clock_timestamp();

INSERT INTO private.ai_model_catalog(model_id, display_name, credit_cost, active, sort_order, provider_metadata)
VALUES ('nano-banana-pro', 'Nano Banana PRO', 10, true, 20, jsonb_build_object('provider','gpti2','type','image'))
ON CONFLICT (model_id) DO UPDATE SET display_name = EXCLUDED.display_name,
  provider_metadata = EXCLUDED.provider_metadata, active = true, updated_at = clock_timestamp();

UPDATE private.ai_model_catalog SET active = false, updated_at = clock_timestamp()
WHERE model_id NOT IN ('gpt-image-2', 'nano-banana-pro');

COMMIT;
