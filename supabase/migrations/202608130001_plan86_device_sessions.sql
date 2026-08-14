BEGIN;

CREATE TABLE private.user_device_sessions (
    session_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL,
    device_id uuid NOT NULL,
    display_name varchar(64) NOT NULL CHECK (
        length(btrim(display_name)) BETWEEN 1 AND 64
        AND display_name !~ '[[:cntrl:]]'),
    client_version varchar(32) NOT NULL CHECK (
        client_version ~ '^[ -~]{1,32}$' AND client_version !~ '[[:cntrl:]]'),
    platform varchar(32) NOT NULL CHECK (
        platform ~ '^[ -~]{1,32}$' AND platform !~ '[[:cntrl:]]'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    last_seen_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    revoked_at timestamptz,
    CONSTRAINT user_device_sessions_user_device_unique UNIQUE (user_id, device_id),
    CHECK (last_seen_at >= created_at),
    CHECK (revoked_at IS NULL OR revoked_at >= created_at)
);

CREATE INDEX user_device_sessions_user_active_idx
    ON private.user_device_sessions(user_id, created_at DESC)
    WHERE revoked_at IS NULL;

ALTER TABLE private.user_device_sessions ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.user_device_sessions FORCE ROW LEVEL SECURITY;

CREATE POLICY user_device_sessions_select_own
ON private.user_device_sessions
FOR SELECT
TO authenticated
USING ((SELECT auth.uid()) = user_id);

REVOKE ALL ON TABLE private.user_device_sessions FROM PUBLIC, anon, authenticated;
GRANT SELECT (
    device_id, session_id, display_name, client_version, platform,
    created_at, last_seen_at, revoked_at
) ON private.user_device_sessions TO authenticated;

CREATE FUNCTION private.device_session_register(
    input_user_id uuid,
    input_device_id uuid,
    input_display_name varchar,
    input_client_version varchar,
    input_platform varchar,
    input_maximum_active_devices integer
)
RETURNS TABLE (
    status_code text,
    device_id uuid,
    session_id uuid,
    display_name varchar,
    client_version varchar,
    platform varchar,
    created_at timestamptz,
    last_seen_at timestamptz,
    revoked_at timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $function$
DECLARE
    existing_session private.user_device_sessions%ROWTYPE;
    inserted_session private.user_device_sessions%ROWTYPE;
    active_count integer;
BEGIN
    IF input_user_id IS NULL OR input_device_id IS NULL
       OR input_maximum_active_devices < 1 OR input_maximum_active_devices > 100
       OR input_display_name IS NULL OR length(btrim(input_display_name)) NOT BETWEEN 1 AND 64
       OR input_display_name ~ '[[:cntrl:]]'
       OR input_client_version IS NULL OR input_client_version !~ '^[ -~]{1,32}$'
       OR input_client_version ~ '[[:cntrl:]]'
       OR input_platform IS NULL OR input_platform !~ '^[ -~]{1,32}$'
       OR input_platform ~ '[[:cntrl:]]' THEN
        RAISE EXCEPTION 'Device session request is invalid.'
            USING ERRCODE = 'P0001', CONSTRAINT = 'DEVICE_SESSION_REQUEST_INVALID';
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended('device-session:' || input_user_id::text, 0));

    SELECT uds.* INTO existing_session
    FROM private.user_device_sessions AS uds
    WHERE uds.user_id = input_user_id AND uds.device_id = input_device_id
    FOR UPDATE;

    IF FOUND THEN
        IF existing_session.revoked_at IS NOT NULL THEN
            RAISE EXCEPTION 'Device session is revoked.'
                USING ERRCODE = 'P0001', CONSTRAINT = 'DEVICE_SESSION_REVOKED';
        END IF;
        RETURN QUERY SELECT 'DEVICE_SESSION_IDEMPOTENT_REPLAY'::text,
            existing_session.device_id, existing_session.session_id, existing_session.display_name,
            existing_session.client_version, existing_session.platform, existing_session.created_at,
            existing_session.last_seen_at, existing_session.revoked_at;
        RETURN;
    END IF;

    SELECT count(*)::integer INTO active_count
    FROM private.user_device_sessions AS uds
    WHERE uds.user_id = input_user_id AND uds.revoked_at IS NULL;
    IF active_count >= input_maximum_active_devices THEN
        RAISE EXCEPTION 'Active device limit reached.'
            USING ERRCODE = 'P0001', CONSTRAINT = 'DEVICE_LIMIT_REACHED';
    END IF;

    INSERT INTO private.user_device_sessions AS uds (
        user_id, device_id, display_name, client_version, platform)
    VALUES (input_user_id, input_device_id, btrim(input_display_name),
        input_client_version, input_platform)
    RETURNING uds.* INTO inserted_session;

    RETURN QUERY SELECT 'DEVICE_SESSION_REGISTERED'::text,
        inserted_session.device_id, inserted_session.session_id, inserted_session.display_name,
        inserted_session.client_version, inserted_session.platform, inserted_session.created_at,
        inserted_session.last_seen_at, inserted_session.revoked_at;
END
$function$;

CREATE FUNCTION private.device_session_validate(
    input_user_id uuid,
    input_device_id uuid,
    input_session_id uuid,
    input_last_seen_write_interval_seconds integer
)
RETURNS TABLE (
    status_code text,
    device_id uuid,
    session_id uuid,
    display_name varchar,
    client_version varchar,
    platform varchar,
    created_at timestamptz,
    last_seen_at timestamptz,
    revoked_at timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $function$
DECLARE
    active_session private.user_device_sessions%ROWTYPE;
BEGIN
    IF input_user_id IS NULL OR input_device_id IS NULL OR input_session_id IS NULL
       OR input_last_seen_write_interval_seconds NOT BETWEEN 60 AND 86400 THEN
        RAISE EXCEPTION 'Device session is inactive.'
            USING ERRCODE = 'P0001', CONSTRAINT = 'DEVICE_SESSION_INACTIVE';
    END IF;

    SELECT uds.* INTO active_session
    FROM private.user_device_sessions AS uds
    WHERE uds.user_id = input_user_id AND uds.device_id = input_device_id
      AND uds.session_id = input_session_id AND uds.revoked_at IS NULL
    FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Device session is inactive.'
            USING ERRCODE = 'P0001', CONSTRAINT = 'DEVICE_SESSION_INACTIVE';
    END IF;

    IF active_session.last_seen_at <= clock_timestamp()
        - make_interval(secs => input_last_seen_write_interval_seconds) THEN
        UPDATE private.user_device_sessions AS uds
        SET last_seen_at = clock_timestamp()
        WHERE uds.session_id = active_session.session_id
        RETURNING uds.* INTO active_session;
    END IF;

    RETURN QUERY SELECT 'DEVICE_SESSION_ACTIVE'::text,
        active_session.device_id, active_session.session_id, active_session.display_name,
        active_session.client_version, active_session.platform, active_session.created_at,
        active_session.last_seen_at, active_session.revoked_at;
END
$function$;

CREATE FUNCTION private.device_session_revoke(
    input_user_id uuid,
    input_session_id uuid
)
RETURNS TABLE (
    status_code text,
    device_id uuid,
    session_id uuid,
    display_name varchar,
    client_version varchar,
    platform varchar,
    created_at timestamptz,
    last_seen_at timestamptz,
    revoked_at timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $function$
DECLARE
    owned_session private.user_device_sessions%ROWTYPE;
    result_code text;
BEGIN
    IF input_user_id IS NULL OR input_session_id IS NULL THEN
        RAISE EXCEPTION 'Device session was not found.'
            USING ERRCODE = 'P0001', CONSTRAINT = 'DEVICE_SESSION_NOT_FOUND';
    END IF;

    SELECT uds.* INTO owned_session
    FROM private.user_device_sessions AS uds
    WHERE uds.user_id = input_user_id AND uds.session_id = input_session_id
    FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Device session was not found.'
            USING ERRCODE = 'P0001', CONSTRAINT = 'DEVICE_SESSION_NOT_FOUND';
    END IF;

    IF owned_session.revoked_at IS NULL THEN
        UPDATE private.user_device_sessions AS uds
        SET revoked_at = clock_timestamp()
        WHERE uds.session_id = owned_session.session_id
        RETURNING uds.* INTO owned_session;
        result_code := 'DEVICE_SESSION_REVOKED';
    ELSE
        result_code := 'DEVICE_SESSION_REVOKE_REPLAY';
    END IF;

    RETURN QUERY SELECT result_code,
        owned_session.device_id, owned_session.session_id, owned_session.display_name,
        owned_session.client_version, owned_session.platform, owned_session.created_at,
        owned_session.last_seen_at, owned_session.revoked_at;
END
$function$;

REVOKE ALL ON FUNCTION private.device_session_register(uuid, uuid, varchar, varchar, varchar, integer)
    FROM PUBLIC, anon, authenticated;
REVOKE ALL ON FUNCTION private.device_session_validate(uuid, uuid, uuid, integer)
    FROM PUBLIC, anon, authenticated;
REVOKE ALL ON FUNCTION private.device_session_revoke(uuid, uuid)
    FROM PUBLIC, anon, authenticated;

GRANT SELECT ON private.user_device_sessions TO service_role;
GRANT EXECUTE ON FUNCTION private.device_session_register(uuid, uuid, varchar, varchar, varchar, integer)
    TO service_role;
GRANT EXECUTE ON FUNCTION private.device_session_validate(uuid, uuid, uuid, integer)
    TO service_role;
GRANT EXECUTE ON FUNCTION private.device_session_revoke(uuid, uuid)
    TO service_role;

COMMIT;
