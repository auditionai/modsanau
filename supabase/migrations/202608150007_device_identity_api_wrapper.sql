BEGIN;
CREATE OR REPLACE FUNCTION public.device_identity_api(action text, payload jsonb DEFAULT '{}'::jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, private
AS $function$
DECLARE
    actor_id uuid := NULLIF(payload->>'userId', '')::uuid;
    device_result record;
BEGIN
    IF current_setting('request.jwt.claim.role', true) IS DISTINCT FROM 'service_role' THEN
        RAISE EXCEPTION 'DEVICE_SERVICE_ROLE_REQUIRED' USING ERRCODE = '42501';
    END IF;
    IF actor_id IS NULL THEN RAISE EXCEPTION 'AUTH_REQUIRED' USING ERRCODE = '42501'; END IF;
    IF action = 'ping' THEN RETURN jsonb_build_object('status', 'ok', 'timestamp', extract(epoch from now())); END IF;
    IF action = 'register' THEN
        SELECT * INTO device_result FROM private.device_profile_register(actor_id);
        IF NOT FOUND THEN RAISE EXCEPTION 'DEVICE_REGISTER_FAILED' USING ERRCODE = 'P0001'; END IF;
        RETURN jsonb_build_object('deviceProfileId', device_result.device_profile_id,
            'publicDeviceCode', device_result.public_device_code, 'deviceStatus', device_result.device_status,
            'subscriptionStatus', device_result.subscription_status, 'subscriptionExpiresAt', device_result.subscription_expires_at,
            'creditBalance', device_result.credit_balance, 'entitlementGrant', device_result.entitlement_grant);
    END IF;
    IF action = 'refresh' THEN
        SELECT dp.device_profile_id, dp.public_device_code, dp.device_status,
            COALESCE(s.status, 'inactive') AS subscription_status, s.expires_at AS subscription_expires_at,
            COALESCE(cw.balance, 0) AS credit_balance
        INTO device_result FROM private.device_profiles dp
        LEFT JOIN private.subscriptions s ON s.device_profile_id = dp.device_profile_id
        LEFT JOIN private.credit_wallet cw ON cw.user_id = dp.owner_user_id
        WHERE dp.owner_user_id = actor_id ORDER BY dp.created_at DESC LIMIT 1;
        IF NOT FOUND THEN RAISE EXCEPTION 'DEVICE_NOT_FOUND' USING ERRCODE = 'P0001'; END IF;
        RETURN jsonb_build_object('deviceProfileId', device_result.device_profile_id,
            'publicDeviceCode', device_result.public_device_code, 'deviceStatus', device_result.device_status,
            'subscriptionStatus', device_result.subscription_status, 'subscriptionExpiresAt', device_result.subscription_expires_at,
            'creditBalance', device_result.credit_balance);
    END IF;
    RAISE EXCEPTION 'UNKNOWN_ACTION: %', action USING ERRCODE = '22023';
END
$function$;
COMMENT ON FUNCTION public.device_identity_api IS 'Device identity operations: register, refresh';
COMMIT;
