BEGIN;

UPDATE private.ai_model_catalog
SET active = false, updated_at = clock_timestamp()
WHERE model_id NOT IN ('gpt-image-2', 'nano-banana-pro');

UPDATE private.ai_model_catalog
SET provider_metadata = jsonb_set(COALESCE(provider_metadata, '{}'::jsonb), '{provider}', '"gpti2"'::jsonb),
    updated_at = clock_timestamp()
WHERE model_id IN ('gpt-image-2', 'nano-banana-pro');

COMMIT;
