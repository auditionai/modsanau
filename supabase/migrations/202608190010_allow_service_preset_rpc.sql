BEGIN;

CREATE OR REPLACE FUNCTION public.ai_image_prompt_preset_api(action text, payload jsonb DEFAULT '{}'::jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, private AS $$
DECLARE
  actor_id uuid := NULLIF(payload->>'adminUserId','')::uuid;
  preset_id_value uuid := NULLIF(payload->>'presetId','')::uuid;
  admin_row private.admin_users%ROWTYPE;
  row_value private.ai_image_prompt_presets%ROWTYPE;
BEGIN
  IF action = 'active_list' THEN
    RETURN COALESCE((SELECT jsonb_agg(jsonb_build_object('presetId',preset_id,'name',display_name,'sortOrder',sort_order) ORDER BY sort_order, display_name) FROM private.ai_image_prompt_presets WHERE is_active AND deleted_at IS NULL),'[]'::jsonb);
  END IF;
  SELECT * INTO admin_row FROM private.admin_users WHERE admin_user_id=actor_id AND is_active;
  IF NOT FOUND OR admin_row.role::text='auditor' OR admin_row.mfa_state::text<>'MFA_VERIFIED' OR NOT COALESCE((payload->>'recentAuth')::boolean,false) THEN RAISE EXCEPTION 'ADMIN_STRONG_AUTH_REQUIRED' USING ERRCODE='42501'; END IF;
  IF action = 'admin_list' THEN
    RETURN COALESCE((SELECT jsonb_agg(jsonb_build_object('presetId',preset_id,'name',display_name,'basePrompt',base_prompt,'active',is_active,'sortOrder',sort_order) ORDER BY sort_order, display_name) FROM private.ai_image_prompt_presets WHERE deleted_at IS NULL),'[]'::jsonb);
  ELSIF action = 'save' THEN
    IF length(trim(COALESCE(payload->>'name',''))) NOT BETWEEN 1 AND 120 OR length(trim(COALESCE(payload->>'basePrompt',''))) NOT BETWEEN 1 AND 12000 THEN RAISE EXCEPTION 'AI_PRESET_INVALID'; END IF;
    IF preset_id_value IS NULL THEN INSERT INTO private.ai_image_prompt_presets(display_name,base_prompt,is_active,sort_order) VALUES(trim(payload->>'name'),trim(payload->>'basePrompt'),COALESCE((payload->>'active')::boolean,true),COALESCE((payload->>'sortOrder')::integer,0)) RETURNING * INTO row_value;
    ELSE UPDATE private.ai_image_prompt_presets SET display_name=trim(payload->>'name'),base_prompt=trim(payload->>'basePrompt'),is_active=COALESCE((payload->>'active')::boolean,is_active),sort_order=COALESCE((payload->>'sortOrder')::integer,sort_order),updated_at=clock_timestamp() WHERE preset_id=preset_id_value AND deleted_at IS NULL RETURNING * INTO row_value; IF NOT FOUND THEN RAISE EXCEPTION 'AI_PRESET_NOT_FOUND'; END IF; END IF;
    PERFORM private.admin_audit_record(actor_id,'AI_IMAGE_PRESET_SAVED','AI_IMAGE_PRESET',row_value.preset_id,gen_random_uuid(),jsonb_build_object('name',row_value.display_name));
    RETURN jsonb_build_object('presetId',row_value.preset_id);
  ELSIF action = 'delete' THEN
    IF preset_id_value IS NULL THEN RAISE EXCEPTION 'AI_PRESET_INVALID'; END IF;
    UPDATE private.ai_image_prompt_presets SET deleted_at=clock_timestamp(),is_active=false,updated_at=clock_timestamp() WHERE preset_id=preset_id_value AND deleted_at IS NULL RETURNING * INTO row_value; IF NOT FOUND THEN RAISE EXCEPTION 'AI_PRESET_NOT_FOUND'; END IF;
    PERFORM private.admin_audit_record(actor_id,'AI_IMAGE_PRESET_DELETED','AI_IMAGE_PRESET',row_value.preset_id,gen_random_uuid(),jsonb_build_object('name',row_value.display_name));
    RETURN jsonb_build_object('presetId',row_value.preset_id);
  END IF;
  RAISE EXCEPTION 'AI_PRESET_ACTION_UNKNOWN';
END $$;

REVOKE ALL ON FUNCTION public.ai_image_prompt_preset_api(text,jsonb) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.ai_image_prompt_preset_api(text,jsonb) TO service_role;
COMMIT;
