BEGIN;

-- PLAN 106: public signup profile, authenticated desktop device gate and private release download.
DO $prerequisites$
BEGIN
    IF to_regclass('private.managed_user_profiles') IS NULL THEN
        RAISE EXCEPTION 'PLAN106_REQUIRES_202608150001_ADMIN_PORTAL_V2';
    END IF;
    IF to_regclass('private.user_device_sessions') IS NULL
       OR to_regprocedure('private.device_session_register(uuid,uuid,character varying,character varying,character varying,integer)') IS NULL
       OR to_regprocedure('private.device_session_validate(uuid,uuid,uuid,integer)') IS NULL THEN
        RAISE EXCEPTION 'PLAN106_REQUIRES_202608130001_PLAN86_DEVICE_SESSIONS';
    END IF;
    IF to_regclass('private.device_profiles') IS NULL
       OR to_regprocedure('private.device_profile_register(uuid)') IS NULL THEN
        RAISE EXCEPTION 'PLAN106_REQUIRES_202608140001_PLAN104_DEVICE_ENTITLEMENTS';
    END IF;
END
$prerequisites$;

CREATE OR REPLACE FUNCTION private.sync_auth_user_profile()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $function$
DECLARE
    requested_name text := NULLIF(left(btrim(COALESCE(new.raw_user_meta_data->>'display_name', '')), 128), '');
BEGIN
    IF requested_name IS NOT NULL AND requested_name ~ '[[:cntrl:]]' THEN
        requested_name := NULL;
    END IF;

    INSERT INTO private.managed_user_profiles(user_id, display_name, contact_email)
    VALUES (new.id, requested_name, lower(new.email))
    ON CONFLICT (user_id) DO UPDATE
    SET display_name = COALESCE(private.managed_user_profiles.display_name, excluded.display_name),
        contact_email = excluded.contact_email,
        updated_at = clock_timestamp();
    RETURN new;
END
$function$;

DROP TRIGGER IF EXISTS sync_auth_user_profile_after_write ON auth.users;
CREATE TRIGGER sync_auth_user_profile_after_write
AFTER INSERT OR UPDATE OF email, raw_user_meta_data ON auth.users
FOR EACH ROW EXECUTE FUNCTION private.sync_auth_user_profile();

-- Backfill existing Auth accounts without overwriting names already managed by Admin.
INSERT INTO private.managed_user_profiles(user_id, display_name, contact_email)
SELECT id,
       CASE WHEN COALESCE(raw_user_meta_data->>'display_name', '') ~ '[[:cntrl:]]' THEN NULL
            ELSE NULLIF(left(btrim(COALESCE(raw_user_meta_data->>'display_name', '')), 128), '') END,
       lower(email)
FROM auth.users
ON CONFLICT (user_id) DO UPDATE
SET contact_email = excluded.contact_email,
    updated_at = clock_timestamp();

CREATE OR REPLACE FUNCTION public.desktop_access_api(action text, payload jsonb DEFAULT '{}'::jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, private
AS $function$
DECLARE
    actor_id uuid := auth.uid();
    actor_email text;
    actor_status text;
    email_is_confirmed boolean;
    requested_device_id uuid;
    requested_session_id uuid;
    session_row record;
    profile_row record;
    app_version text;
    device_label text;
    platform_name text;
BEGIN
    IF actor_id IS NULL THEN
        RAISE EXCEPTION 'AUTH_REQUIRED' USING ERRCODE = '42501';
    END IF;

    SELECT lower(email), email_confirmed_at IS NOT NULL
    INTO actor_email, email_is_confirmed
    FROM auth.users WHERE id = actor_id;
    IF NOT FOUND OR NOT email_is_confirmed THEN
        RAISE EXCEPTION 'EMAIL_CONFIRMATION_REQUIRED' USING ERRCODE = '42501';
    END IF;

    SELECT COALESCE(status::text, 'active') INTO actor_status
    FROM private.managed_user_profiles WHERE user_id = actor_id;
    IF actor_status IS DISTINCT FROM 'active' THEN
        RAISE EXCEPTION 'USER_INACTIVE' USING ERRCODE = '42501';
    END IF;

    IF action NOT IN ('register', 'validate') THEN
        RAISE EXCEPTION 'DESKTOP_ACTION_UNKNOWN';
    END IF;

    requested_device_id := NULLIF(payload->>'deviceId', '')::uuid;
    requested_session_id := NULLIF(payload->>'sessionId', '')::uuid;
    device_label := left(btrim(COALESCE(payload->>'displayName', 'Máy Windows')), 64);
    app_version := left(btrim(COALESCE(payload->>'clientVersion', 'unknown')), 32);
    platform_name := left(btrim(COALESCE(payload->>'platform', 'windows-x64')), 32);

    IF requested_device_id IS NULL OR device_label = '' OR app_version = '' OR platform_name = ''
       OR device_label ~ '[[:cntrl:]]' OR app_version !~ '^[ -~]{1,32}$'
       OR platform_name !~ '^[ -~]{1,32}$' THEN
        RAISE EXCEPTION 'DEVICE_REQUEST_INVALID';
    END IF;

    IF action = 'register' THEN
        SELECT * INTO session_row FROM private.device_session_register(
            actor_id, requested_device_id, device_label, app_version, platform_name, 5);
        SELECT * INTO profile_row FROM private.device_profile_register(actor_id);
    ELSE
        IF requested_session_id IS NULL THEN RAISE EXCEPTION 'DEVICE_SESSION_REQUIRED'; END IF;
        SELECT * INTO session_row FROM private.device_session_validate(
            actor_id, requested_device_id, requested_session_id, 60);
        SELECT * INTO profile_row FROM private.device_profile_register(actor_id);
    END IF;

    IF profile_row.device_status <> 'active' THEN
        RAISE EXCEPTION 'DEVICE_INACTIVE' USING ERRCODE = '42501';
    END IF;

    RETURN jsonb_build_object(
        'code', session_row.status_code,
        'userId', actor_id,
        'email', actor_email,
        'deviceId', session_row.device_id,
        'sessionId', session_row.session_id,
        'publicDeviceCode', profile_row.public_device_code,
        'deviceStatus', profile_row.device_status,
        'lastSeenAt', session_row.last_seen_at);
END
$function$;

REVOKE ALL ON FUNCTION public.desktop_access_api(text, jsonb) FROM PUBLIC, anon;
GRANT EXECUTE ON FUNCTION public.desktop_access_api(text, jsonb) TO authenticated;

CREATE OR REPLACE FUNCTION public.release_download_allowed()
RETURNS boolean
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
    SELECT EXISTS (
        SELECT 1 FROM auth.users
        WHERE id = auth.uid() AND email_confirmed_at IS NOT NULL
    );
$function$;

REVOKE ALL ON FUNCTION public.release_download_allowed() FROM PUBLIC, anon;
GRANT EXECUTE ON FUNCTION public.release_download_allowed() TO authenticated;

-- Release objects are private. Authenticated, email-confirmed users may read only the approved path.
INSERT INTO storage.buckets(id, name, public, file_size_limit, allowed_mime_types)
VALUES ('desktop-releases', 'desktop-releases', false, 1073741824,
        ARRAY['application/zip', 'application/octet-stream', 'application/msix'])
ON CONFLICT (id) DO UPDATE SET public = false;

DROP POLICY IF EXISTS desktop_release_confirmed_download ON storage.objects;
CREATE POLICY desktop_release_confirmed_download
ON storage.objects FOR SELECT TO authenticated
USING (
    bucket_id = 'desktop-releases'
    AND name = 'stable/AuditionAI-Mod-Studio-win-x64.zip'
    AND public.release_download_allowed()
);

COMMIT;
