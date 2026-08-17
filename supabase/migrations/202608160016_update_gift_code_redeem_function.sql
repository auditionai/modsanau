BEGIN;

-- Recreate gift_code_redeem function với hỗ trợ:
-- 1. Hybrid gift code (vừa credits vừa duration)
-- 2. Device fingerprint tracking để chặn spam tài khoản mới
CREATE OR REPLACE FUNCTION private.gift_code_redeem(
    input_user_id uuid,
    input_gift_code text,
    input_correlation_id uuid,
    input_device_fingerprint varchar DEFAULT NULL
)
RETURNS TABLE (
    device_profile_id uuid,
    public_device_code varchar,
    device_status text,
    subscription_status text,
    subscription_expires_at timestamptz,
    can_use_ai boolean,
    can_build boolean,
    can_export boolean,
    can_use_premium_templates boolean,
    available_credits bigint,
    observed_at timestamptz
)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, private, extensions
AS $function$
DECLARE
    profile private.device_profiles%ROWTYPE;
    gift private.gift_codes%ROWTYPE;
    now_at timestamptz := clock_timestamp();
    base_at timestamptz;
BEGIN
    -- Validate input
    IF input_user_id IS NULL OR input_correlation_id IS NULL OR input_gift_code IS NULL
       OR length(input_gift_code) NOT BETWEEN 8 AND 128
       OR input_gift_code !~ '^[A-Za-z0-9-]+$' THEN
        RAISE EXCEPTION 'Gift code request is invalid.'
            USING ERRCODE='P0001', CONSTRAINT='GIFT_CODE_INVALID';
    END IF;

    -- Get device profile
    SELECT * INTO profile FROM private.device_profiles WHERE auth_user_id = input_user_id FOR UPDATE;
    IF NOT FOUND OR profile.status <> 'active' THEN
        RAISE EXCEPTION 'Device is inactive.'
            USING ERRCODE='P0001', CONSTRAINT='DEVICE_INACTIVE';
    END IF;

    -- Update device fingerprint nếu được cung cấp
    IF input_device_fingerprint IS NOT NULL AND input_device_fingerprint <> '' THEN
        UPDATE private.device_profiles
        SET device_fingerprint = input_device_fingerprint
        WHERE device_profile_id = profile.device_profile_id;
        profile.device_fingerprint := input_device_fingerprint;
    END IF;

    -- Get gift code
    SELECT * INTO gift FROM private.gift_codes
    WHERE code_sha256 = digest(upper(btrim(input_gift_code)), 'sha256') FOR UPDATE;

    IF NOT FOUND OR gift.disabled_at IS NOT NULL
       OR (gift.expires_at IS NOT NULL AND gift.expires_at <= now_at)
       OR gift.redemption_count >= gift.maximum_redemptions THEN
        RAISE EXCEPTION 'Gift code is unavailable.'
            USING ERRCODE='P0001', CONSTRAINT='GIFT_CODE_UNAVAILABLE';
    END IF;

    -- Check if already redeemed by this device_profile_id
    IF EXISTS(
        SELECT 1 FROM private.gift_code_redemptions
        WHERE gift_code_id = gift.gift_code_id
        AND device_profile_id = profile.device_profile_id
    ) THEN
        RAISE EXCEPTION 'Gift code already redeemed.'
            USING ERRCODE='P0001', CONSTRAINT='GIFT_CODE_ALREADY_REDEEMED';
    END IF;

    -- Check if already redeemed by this device_fingerprint (chặn spam tài khoản mới)
    IF profile.device_fingerprint IS NOT NULL THEN
        IF EXISTS(
            SELECT 1 FROM private.gift_code_redemptions
            WHERE gift_code_id = gift.gift_code_id
            AND device_fingerprint = profile.device_fingerprint
        ) THEN
            RAISE EXCEPTION 'Gift code already used on this device.'
                USING ERRCODE='P0001', CONSTRAINT='GIFT_CODE_DEVICE_LIMIT';
        END IF;
    END IF;

    -- Apply duration (nếu có và > 0)
    IF COALESCE(gift.duration_days, 0) > 0 THEN
        SELECT greatest(now_at, expires_at) INTO base_at
        FROM private.subscriptions
        WHERE device_profile_id = profile.device_profile_id FOR UPDATE;

        base_at := COALESCE(base_at, now_at);

        INSERT INTO private.subscriptions(device_profile_id, status, starts_at, expires_at)
        VALUES(profile.device_profile_id, 'active', now_at, base_at + make_interval(days => gift.duration_days))
        ON CONFLICT(device_profile_id) DO UPDATE SET
            status = 'active',
            expires_at = greatest(private.subscriptions.expires_at, now_at) + make_interval(days => gift.duration_days),
            updated_at = now_at;
    END IF;

    -- Apply credits (nếu có và > 0)
    IF COALESCE(gift.credit_amount, 0) > 0 THEN
        PERFORM private.credit_grant(
            input_user_id,
            gift.credit_amount,
            'gift_code:' || gift.gift_code_id::text,
            input_correlation_id::text,
            upper(encode(digest(gift.gift_code_id::text || ':' || gift.credit_amount::text, 'sha256'), 'hex'))
        );
    END IF;

    -- Record redemption
    INSERT INTO private.gift_code_redemptions(
        gift_code_id,
        device_profile_id,
        duration_days,
        credit_amount,
        device_fingerprint
    )
    VALUES(
        gift.gift_code_id,
        profile.device_profile_id,
        gift.duration_days,
        gift.credit_amount,
        profile.device_fingerprint
    );

    -- Increment redemption count
    UPDATE private.gift_codes
    SET redemption_count = redemption_count + 1
    WHERE gift_code_id = gift.gift_code_id;

    -- Log event
    INSERT INTO private.device_commercial_events(device_profile_id, event_type, correlation_id, metadata)
    VALUES(
        profile.device_profile_id,
        'GIFT_CODE_REDEEMED',
        input_correlation_id,
        jsonb_build_object('kind', gift.kind::text, 'giftCodeId', gift.gift_code_id)
    );

    -- Return updated entitlements
    RETURN QUERY SELECT * FROM private.device_profile_register(input_user_id);
END $function$;

-- Grant permissions
REVOKE ALL ON FUNCTION private.gift_code_redeem(uuid, text, uuid, varchar) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION private.gift_code_redeem(uuid, text, uuid, varchar) TO service_role;

COMMIT;
