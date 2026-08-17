BEGIN;

-- Gemini's shared Vertex AI publisher endpoint is global. The hostname is
-- handled by the Edge Function; persisted rows keep this location value.
ALTER TABLE private.vertex_ai_credentials
    DROP CONSTRAINT vertex_ai_credentials_region_check;

ALTER TABLE private.vertex_ai_credentials
    ALTER COLUMN region SET DEFAULT 'global';

UPDATE private.vertex_ai_credentials
SET region = 'global',
    updated_at = clock_timestamp()
WHERE region <> 'global';

ALTER TABLE private.vertex_ai_credentials
    ADD CONSTRAINT vertex_ai_credentials_region_check CHECK (region = 'global');

UPDATE private.ai_provider_config
SET region = 'global',
    updated_at = clock_timestamp()
WHERE provider_id = 'vertex_ai'
  AND region <> 'global';

COMMIT;
