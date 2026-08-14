BEGIN;

CREATE TYPE private.device_commercial_status AS ENUM ('active', 'blocked', 'revoked');
CREATE TYPE private.subscription_status AS ENUM ('active', 'suspended', 'revoked');
CREATE TYPE private.gift_code_kind AS ENUM ('duration', 'credits');

CREATE TABLE private.device_profiles (
    device_profile_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    auth_user_id uuid NOT NULL UNIQUE,
    public_device_code varchar(18) NOT NULL UNIQUE CHECK (
        public_device_code ~ '^AMS-[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{4}$'),
    status private.device_commercial_status NOT NULL DEFAULT 'active',
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    last_seen_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK (last_seen_at >= created_at)
);

CREATE TABLE private.subscriptions (
    subscription_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    device_profile_id uuid NOT NULL UNIQUE REFERENCES private.device_profiles(device_profile_id),
    status private.subscription_status NOT NULL DEFAULT 'active',
    starts_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK (expires_at > starts_at),
    CHECK (updated_at >= created_at)
);

CREATE TABLE private.gift_codes (
    gift_code_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code_prefix varchar(12) NOT NULL,
    code_sha256 bytea NOT NULL UNIQUE CHECK (octet_length(code_sha256) = 32),
    kind private.gift_code_kind NOT NULL,
    duration_days integer CHECK (duration_days BETWEEN 1 AND 3650),
    credit_amount bigint CHECK (credit_amount BETWEEN 1 AND 1000000000),
    maximum_redemptions integer NOT NULL DEFAULT 1 CHECK (maximum_redemptions BETWEEN 1 AND 1000000),
    redemption_count integer NOT NULL DEFAULT 0 CHECK (redemption_count BETWEEN 0 AND maximum_redemptions),
    expires_at timestamptz,
    disabled_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK ((kind = 'duration' AND duration_days IS NOT NULL AND credit_amount IS NULL)
        OR (kind = 'credits' AND credit_amount IS NOT NULL AND duration_days IS NULL))
);

CREATE TABLE private.gift_code_redemptions (
    redemption_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    gift_code_id uuid NOT NULL REFERENCES private.gift_codes(gift_code_id),
    device_profile_id uuid NOT NULL REFERENCES private.device_profiles(device_profile_id),
    redeemed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    duration_days integer,
    credit_amount bigint,
    UNIQUE (gift_code_id, device_profile_id)
);

CREATE TABLE private.device_commercial_events (
    event_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    device_profile_id uuid NOT NULL REFERENCES private.device_profiles(device_profile_id),
    event_type varchar(64) NOT NULL CHECK (event_type ~ '^[A-Z0-9_]{1,64}$'),
    event_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    correlation_id uuid NOT NULL UNIQUE,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(metadata) = 'object')
);

CREATE INDEX subscriptions_expiry_idx ON private.subscriptions(expires_at);
CREATE INDEX gift_codes_prefix_idx ON private.gift_codes(code_prefix);
CREATE INDEX device_events_profile_time_idx ON private.device_commercial_events(device_profile_id, event_at DESC);

ALTER TABLE private.device_profiles ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.device_profiles FORCE ROW LEVEL SECURITY;
ALTER TABLE private.subscriptions ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.subscriptions FORCE ROW LEVEL SECURITY;
ALTER TABLE private.gift_codes ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.gift_codes FORCE ROW LEVEL SECURITY;
ALTER TABLE private.gift_code_redemptions ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.gift_code_redemptions FORCE ROW LEVEL SECURITY;
ALTER TABLE private.device_commercial_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.device_commercial_events FORCE ROW LEVEL SECURITY;

REVOKE ALL ON private.device_profiles, private.subscriptions, private.gift_codes,
    private.gift_code_redemptions, private.device_commercial_events FROM PUBLIC, anon, authenticated;
GRANT SELECT, INSERT, UPDATE ON private.device_profiles, private.subscriptions, private.gift_codes,
    private.gift_code_redemptions, private.device_commercial_events TO service_role;

CREATE FUNCTION private.device_profile_register(input_user_id uuid)
RETURNS TABLE (device_profile_id uuid, public_device_code varchar, device_status text,
    subscription_status text, subscription_expires_at timestamptz,
    can_use_ai boolean, can_build boolean, can_export boolean, can_use_premium_templates boolean,
    available_credits bigint, observed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, private
AS $function$
DECLARE profile private.device_profiles%ROWTYPE; now_at timestamptz := clock_timestamp();
DECLARE alphabet constant text := '23456789ABCDEFGHJKMNPQRSTUVWXYZ'; candidate text; i integer;
BEGIN
    IF input_user_id IS NULL THEN RAISE EXCEPTION 'Invalid device profile request.'
        USING ERRCODE='P0001', CONSTRAINT='DEVICE_PROFILE_REQUEST_INVALID'; END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('device-profile:' || input_user_id::text, 0));
    SELECT * INTO profile FROM private.device_profiles WHERE auth_user_id=input_user_id FOR UPDATE;
    IF NOT FOUND THEN
        LOOP
            candidate := 'AMS-';
            FOR i IN 1..12 LOOP
                candidate := candidate || substr(alphabet, 1 + floor(random()*length(alphabet))::integer, 1);
                IF i IN (4,8) THEN candidate := candidate || '-'; END IF;
            END LOOP;
            BEGIN
                INSERT INTO private.device_profiles(auth_user_id, public_device_code)
                VALUES(input_user_id, candidate) RETURNING * INTO profile; EXIT;
            EXCEPTION WHEN unique_violation THEN END;
        END LOOP;
        INSERT INTO private.device_commercial_events(device_profile_id,event_type,correlation_id)
        VALUES(profile.device_profile_id,'DEVICE_REGISTERED',gen_random_uuid());
    ELSE
        UPDATE private.device_profiles SET last_seen_at=now_at
        WHERE private.device_profiles.device_profile_id=profile.device_profile_id RETURNING * INTO profile;
    END IF;
    RETURN QUERY SELECT profile.device_profile_id, profile.public_device_code, profile.status::text,
        CASE WHEN s.status='active' AND s.expires_at>now_at THEN 'active'
             WHEN s.subscription_id IS NULL THEN 'none' ELSE s.status::text END,
        s.expires_at,
        profile.status='active' AND s.status='active' AND s.expires_at>now_at,
        profile.status='active' AND s.status='active' AND s.expires_at>now_at,
        profile.status='active' AND s.status='active' AND s.expires_at>now_at,
        profile.status='active' AND s.status='active' AND s.expires_at>now_at,
        COALESCE(w.available_credits,0), now_at
    FROM (SELECT 1) AS anchor
    LEFT JOIN private.subscriptions s ON s.device_profile_id=profile.device_profile_id
    LEFT JOIN private.credit_wallets w ON w.user_id=input_user_id;
END $function$;

CREATE FUNCTION private.gift_code_redeem(input_user_id uuid, input_gift_code text, input_correlation_id uuid)
RETURNS TABLE (device_profile_id uuid, public_device_code varchar, device_status text,
    subscription_status text, subscription_expires_at timestamptz,
    can_use_ai boolean, can_build boolean, can_export boolean, can_use_premium_templates boolean,
    available_credits bigint, observed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, private, extensions
AS $function$
DECLARE profile private.device_profiles%ROWTYPE; gift private.gift_codes%ROWTYPE; now_at timestamptz:=clock_timestamp(); base_at timestamptz;
BEGIN
    IF input_user_id IS NULL OR input_correlation_id IS NULL OR input_gift_code IS NULL
       OR length(input_gift_code) NOT BETWEEN 8 AND 128 OR input_gift_code !~ '^[A-Za-z0-9-]+$' THEN
       RAISE EXCEPTION 'Gift code request is invalid.' USING ERRCODE='P0001', CONSTRAINT='GIFT_CODE_INVALID'; END IF;
    SELECT * INTO profile FROM private.device_profiles WHERE auth_user_id=input_user_id FOR UPDATE;
    IF NOT FOUND OR profile.status<>'active' THEN RAISE EXCEPTION 'Device is inactive.'
       USING ERRCODE='P0001', CONSTRAINT='DEVICE_INACTIVE'; END IF;
    SELECT * INTO gift FROM private.gift_codes
      WHERE code_sha256=digest(upper(btrim(input_gift_code)),'sha256') FOR UPDATE;
    IF NOT FOUND OR gift.disabled_at IS NOT NULL OR gift.expires_at<=now_at
       OR gift.redemption_count>=gift.maximum_redemptions THEN RAISE EXCEPTION 'Gift code is unavailable.'
       USING ERRCODE='P0001', CONSTRAINT='GIFT_CODE_UNAVAILABLE'; END IF;
    IF EXISTS(SELECT 1 FROM private.gift_code_redemptions WHERE gift_code_id=gift.gift_code_id
       AND device_profile_id=profile.device_profile_id) THEN RAISE EXCEPTION 'Gift code already redeemed.'
       USING ERRCODE='P0001', CONSTRAINT='GIFT_CODE_ALREADY_REDEEMED'; END IF;
    IF gift.kind='duration' THEN
        SELECT greatest(now_at, expires_at) INTO base_at FROM private.subscriptions
            WHERE device_profile_id=profile.device_profile_id FOR UPDATE;
        base_at := COALESCE(base_at, now_at);
        INSERT INTO private.subscriptions(device_profile_id,status,starts_at,expires_at)
        VALUES(profile.device_profile_id,'active',now_at,base_at+make_interval(days=>gift.duration_days))
        ON CONFLICT(device_profile_id) DO UPDATE SET status='active',
            expires_at=greatest(private.subscriptions.expires_at,now_at)+make_interval(days=>gift.duration_days),
            updated_at=now_at;
    ELSE
        PERFORM private.credit_grant(input_user_id,gift.credit_amount,
            'gift_code:' || gift.gift_code_id::text,
            input_correlation_id::text,
            upper(encode(digest(gift.gift_code_id::text || ':' || gift.credit_amount::text,'sha256'),'hex')));
    END IF;
    INSERT INTO private.gift_code_redemptions(gift_code_id,device_profile_id,duration_days,credit_amount)
    VALUES(gift.gift_code_id,profile.device_profile_id,gift.duration_days,gift.credit_amount);
    UPDATE private.gift_codes SET redemption_count=redemption_count+1 WHERE gift_code_id=gift.gift_code_id;
    INSERT INTO private.device_commercial_events(device_profile_id,event_type,correlation_id,metadata)
    VALUES(profile.device_profile_id,'GIFT_CODE_REDEEMED',input_correlation_id,
        jsonb_build_object('kind',gift.kind::text,'giftCodeId',gift.gift_code_id));
    RETURN QUERY SELECT * FROM private.device_profile_register(input_user_id);
END $function$;

REVOKE ALL ON FUNCTION private.device_profile_register(uuid) FROM PUBLIC, anon, authenticated;
REVOKE ALL ON FUNCTION private.gift_code_redeem(uuid,text,uuid) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION private.device_profile_register(uuid) TO service_role;
GRANT EXECUTE ON FUNCTION private.gift_code_redeem(uuid,text,uuid) TO service_role;

COMMIT;
