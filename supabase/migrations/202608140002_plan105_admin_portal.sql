BEGIN;

CREATE TYPE private.admin_role AS ENUM ('owner', 'operator', 'auditor');
CREATE TYPE private.admin_mfa_state AS ENUM ('MFA_NOT_VERIFIED', 'MFA_PENDING', 'MFA_VERIFIED');

CREATE TABLE private.admin_users (
    admin_user_id uuid PRIMARY KEY REFERENCES auth.users(id) ON DELETE CASCADE,
    role private.admin_role NOT NULL DEFAULT 'operator',
    mfa_state private.admin_mfa_state NOT NULL DEFAULT 'MFA_NOT_VERIFIED',
    bootstrap_source text NOT NULL DEFAULT 'manual',
    display_label text,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    last_seen_at timestamptz,
    CHECK (display_label IS NULL OR (length(display_label) BETWEEN 1 AND 128 AND NOT display_label ~ '[[:cntrl:]]'))
);

CREATE TABLE private.admin_audit_events (
    event_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    actor_admin_user_id uuid NOT NULL REFERENCES private.admin_users(admin_user_id) ON DELETE CASCADE,
    event_type text NOT NULL CHECK (event_type ~ '^[A-Z][A-Z0-9_]{2,63}$'),
    target_kind text NOT NULL CHECK (target_kind ~ '^[A-Z][A-Z0-9_]{2,63}$'),
    target_id uuid,
    correlation_id uuid NOT NULL UNIQUE,
    details jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(details) = 'object'),
    event_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX admin_users_active_idx
    ON private.admin_users(is_active, role, updated_at DESC);
CREATE INDEX admin_audit_events_time_idx
    ON private.admin_audit_events(event_at DESC, event_id DESC);
CREATE INDEX admin_audit_events_actor_idx
    ON private.admin_audit_events(actor_admin_user_id, event_at DESC);
CREATE INDEX admin_audit_events_target_idx
    ON private.admin_audit_events(target_kind, target_id, event_at DESC);

ALTER TABLE private.admin_users ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.admin_users FORCE ROW LEVEL SECURITY;
ALTER TABLE private.admin_audit_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.admin_audit_events FORCE ROW LEVEL SECURITY;

REVOKE ALL ON private.admin_users, private.admin_audit_events FROM PUBLIC, anon, authenticated;
GRANT SELECT, INSERT, UPDATE ON private.admin_users, private.admin_audit_events TO service_role;

CREATE OR REPLACE FUNCTION private.admin_audit_record(
    p_actor_admin_user_id uuid,
    p_event_type text,
    p_target_kind text,
    p_target_id uuid,
    p_correlation_id uuid,
    p_details jsonb)
RETURNS TABLE(event_id uuid, event_at timestamptz)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $$
DECLARE
    new_event_id uuid := gen_random_uuid();
    new_event_at timestamptz := clock_timestamp();
BEGIN
    IF p_actor_admin_user_id IS NULL
        OR p_actor_admin_user_id = '00000000-0000-0000-0000-000000000000'::uuid
        OR p_event_type IS NULL OR p_event_type !~ '^[A-Z][A-Z0-9_]{2,63}$'
        OR p_target_kind IS NULL OR p_target_kind !~ '^[A-Z][A-Z0-9_]{2,63}$'
        OR p_correlation_id IS NULL
        OR p_details IS NULL OR jsonb_typeof(p_details) <> 'object'
    THEN
        RAISE EXCEPTION 'admin audit event invalid'
            USING ERRCODE = 'P0001', CONSTRAINT = 'ADMIN_AUDIT_EVENT_INVALID';
    END IF;

    INSERT INTO private.admin_audit_events(
        event_id, actor_admin_user_id, event_type, target_kind, target_id, correlation_id, details, event_at)
    VALUES (
        new_event_id, p_actor_admin_user_id, p_event_type, p_target_kind, p_target_id, p_correlation_id, p_details,
        new_event_at);

    RETURN QUERY SELECT new_event_id, new_event_at;
END;
$$;

CREATE OR REPLACE FUNCTION private.admin_bootstrap_first_user(
    p_user_id uuid,
    p_role private.admin_role DEFAULT 'owner',
    p_mfa_state private.admin_mfa_state DEFAULT 'MFA_NOT_VERIFIED',
    p_display_label text DEFAULT NULL,
    p_bootstrap_source text DEFAULT 'existing_supabase_user')
RETURNS TABLE(
    admin_user_id uuid,
    role text,
    mfa_state text,
    bootstrap_source text,
    display_label text,
    is_active boolean,
    created_at timestamptz,
    updated_at timestamptz,
    last_seen_at timestamptz,
    bootstrap_created boolean)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $$
DECLARE
    admin_count bigint;
    existing private.admin_users%ROWTYPE;
BEGIN
    IF p_user_id IS NULL OR p_user_id = '00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'admin bootstrap user invalid'
            USING ERRCODE = 'P0001', CONSTRAINT = 'ADMIN_BOOTSTRAP_INVALID';
    END IF;

    IF p_display_label IS NOT NULL
        AND (length(p_display_label) NOT BETWEEN 1 AND 128 OR p_display_label ~ '[[:cntrl:]]') THEN
        RAISE EXCEPTION 'admin display label invalid'
            USING ERRCODE = 'P0001', CONSTRAINT = 'ADMIN_BOOTSTRAP_INVALID';
    END IF;

    SELECT count(*) INTO admin_count FROM private.admin_users;
    IF admin_count > 0 THEN
        SELECT * INTO existing FROM private.admin_users WHERE admin_user_id = p_user_id;
        IF existing.admin_user_id IS NULL THEN
            RETURN;
        END IF;

        UPDATE private.admin_users
            SET updated_at = clock_timestamp(),
                last_seen_at = clock_timestamp(),
                display_label = COALESCE(p_display_label, display_label)
            WHERE admin_user_id = p_user_id
            RETURNING * INTO existing;

        RETURN QUERY SELECT existing.admin_user_id, existing.role::text, existing.mfa_state::text,
            existing.bootstrap_source, existing.display_label, existing.is_active, existing.created_at,
            existing.updated_at, existing.last_seen_at, false;
        RETURN;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM auth.users WHERE id = p_user_id) THEN
        RAISE EXCEPTION 'admin bootstrap user missing from auth.users'
            USING ERRCODE = 'P0001', CONSTRAINT = 'ADMIN_BOOTSTRAP_USER_MISSING';
    END IF;

    INSERT INTO private.admin_users(
        admin_user_id, role, mfa_state, bootstrap_source, display_label, is_active, last_seen_at)
    VALUES (
        p_user_id, p_role, p_mfa_state, p_bootstrap_source, p_display_label, true, clock_timestamp())
    RETURNING * INTO existing;

    PERFORM private.admin_audit_record(
        existing.admin_user_id,
        'ADMIN_BOOTSTRAPPED',
        'ADMIN_USER',
        existing.admin_user_id,
        gen_random_uuid(),
        jsonb_build_object(
            'role', existing.role::text,
            'mfaState', existing.mfa_state::text,
            'bootstrapSource', existing.bootstrap_source));

    RETURN QUERY SELECT existing.admin_user_id, existing.role::text, existing.mfa_state::text,
        existing.bootstrap_source, existing.display_label, existing.is_active, existing.created_at,
        existing.updated_at, existing.last_seen_at, true;
END;
$$;

REVOKE ALL ON FUNCTION private.admin_audit_record(uuid, text, text, uuid, uuid, jsonb) FROM PUBLIC, anon, authenticated;
REVOKE ALL ON FUNCTION private.admin_bootstrap_first_user(uuid, private.admin_role,
    private.admin_mfa_state, text, text) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION private.admin_audit_record(uuid, text, text, uuid, uuid, jsonb) TO service_role;
GRANT EXECUTE ON FUNCTION private.admin_bootstrap_first_user(uuid, private.admin_role,
    private.admin_mfa_state, text, text) TO service_role;

COMMIT;
