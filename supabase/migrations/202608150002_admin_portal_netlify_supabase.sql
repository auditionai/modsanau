BEGIN;

-- Admin Portal chạy trực tiếp trên Supabase Data API. Hàm duy nhất này là
-- security boundary: user id luôn lấy từ JWT, không nhận từ frontend.
CREATE OR REPLACE FUNCTION public.admin_portal_api(action text, payload jsonb DEFAULT '{}'::jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public, private
AS $function$
DECLARE
    actor_id uuid := auth.uid();
    actor_email text;
    actor_role text;
    result jsonb;
    target_id uuid;
    correlation uuid;
    reason text;
    generated_code text;
    saved_package_id uuid;
    days integer;
    page_limit integer;
    page_offset integer;
    search_query text;
    status_filter text;
BEGIN
    IF actor_id IS NULL THEN
        RAISE EXCEPTION 'ADMIN_AUTH_REQUIRED' USING ERRCODE = '42501';
    END IF;

    SELECT lower(email) INTO actor_email FROM auth.users WHERE id = actor_id;

    -- Bootstrap chỉ được phép đúng một lần và chỉ cho owner đã phê duyệt.
    IF NOT EXISTS (SELECT 1 FROM private.admin_users) THEN
        IF actor_email <> 'codycn2804@gmail.com' THEN
            RAISE EXCEPTION 'ADMIN_ACCESS_REQUIRED' USING ERRCODE = '42501';
        END IF;
        PERFORM 1 FROM private.admin_bootstrap_first_user(
            actor_id, 'owner'::private.admin_role, 'MFA_NOT_VERIFIED'::private.admin_mfa_state,
            actor_email, 'supabase_direct');
    END IF;

    SELECT role::text INTO actor_role
    FROM private.admin_users
    WHERE admin_user_id = actor_id AND is_active;
    IF actor_role IS NULL THEN
        RAISE EXCEPTION 'ADMIN_ACCESS_REQUIRED' USING ERRCODE = '42501';
    END IF;

    UPDATE private.admin_users
    SET last_seen_at = clock_timestamp(), updated_at = clock_timestamp()
    WHERE admin_user_id = actor_id;

    IF action = 'session' THEN
        RETURN jsonb_build_object(
            'code', 'ADMIN_SESSION_ACTIVE',
            'admin', jsonb_build_object(
                'adminUserId', actor_id, 'email', actor_email, 'role', actor_role,
                'mfaState', (SELECT mfa_state::text FROM private.admin_users WHERE admin_user_id=actor_id),
                'displayLabel', (SELECT display_label FROM private.admin_users WHERE admin_user_id=actor_id),
                'isActive', true,
                'lastSeenAt', clock_timestamp()));
    END IF;

    days := LEAST(GREATEST(COALESCE((payload->>'days')::integer, 30), 7), 90);
    page_limit := LEAST(GREATEST(COALESCE((payload->>'limit')::integer, 100), 1), 100);
    page_offset := GREATEST(COALESCE((payload->>'offset')::integer, 0), 0);
    search_query := NULLIF(left(btrim(COALESCE(payload->>'query','')), 128), '');
    status_filter := NULLIF(left(btrim(COALESCE(payload->>'status','')), 32), '');

    IF action = 'analytics' THEN
        WITH dates AS (
            SELECT generate_series(current_date - (days - 1), current_date, interval '1 day')::date day
        ), users_by_day AS (
            SELECT created_at::date day, count(*) total FROM auth.users
            WHERE created_at >= current_date - (days - 1) GROUP BY created_at::date
        ), payments_by_day AS (
            SELECT verified_at::date day, count(*) total, COALESCE(sum(amount_minor),0) revenue
            FROM private.payment_events WHERE verified_at >= current_date - (days - 1)
            GROUP BY verified_at::date
        )
        SELECT jsonb_build_object(
            'totalUsers', (SELECT count(*) FROM auth.users),
            'newUsers30Days', (SELECT count(*) FROM auth.users WHERE created_at >= clock_timestamp()-interval '30 days'),
            'activeDevices', (SELECT count(*) FROM private.device_profiles WHERE status='active'),
            'blockedDevices', (SELECT count(*) FROM private.device_profiles WHERE status='blocked'),
            'revokedDevices', (SELECT count(*) FROM private.device_profiles WHERE status='revoked'),
            'activeSubscriptions', (SELECT count(*) FROM private.subscriptions WHERE status='active' AND expires_at>clock_timestamp()),
            'expiringSubscriptions7Days', (SELECT count(*) FROM private.subscriptions WHERE status='active' AND expires_at>clock_timestamp() AND expires_at<=clock_timestamp()+interval '7 days'),
            'totalTransactions', (SELECT count(*) FROM private.payment_events),
            'revenueMinor', (SELECT COALESCE(sum(amount_minor),0) FROM private.payment_events),
            'creditsSold', (SELECT COALESCE(sum(credits),0) FROM private.payment_events),
            'activeGiftCodes', (SELECT count(*) FROM private.gift_codes WHERE disabled_at IS NULL AND (expires_at IS NULL OR expires_at>clock_timestamp()) AND redemption_count<maximum_redemptions),
            'giftCodeRedemptions', (SELECT count(*) FROM private.gift_code_redemptions),
            'currency', COALESCE((SELECT currency::text FROM private.payment_events GROUP BY currency ORDER BY count(*) DESC LIMIT 1),'vnd'),
            'observedAt', clock_timestamp(),
            'daily', (SELECT COALESCE(jsonb_agg(jsonb_build_object('day',d.day,'users',COALESCE(u.total,0),'transactions',COALESCE(p.total,0),'revenueMinor',COALESCE(p.revenue,0)) ORDER BY d.day),'[]'::jsonb)
                FROM dates d LEFT JOIN users_by_day u USING(day) LEFT JOIN payments_by_day p USING(day))
        ) INTO result;
        RETURN result;
    END IF;

    IF action = 'dashboard' THEN
        SELECT jsonb_build_object(
            'recentAudit', COALESCE(jsonb_agg(jsonb_build_object(
                'eventId', event_id, 'actorAdminUserId', actor_admin_user_id,
                'eventType', event_type, 'targetKind', target_kind, 'targetId', target_id,
                'correlationId', correlation_id, 'details', details::text, 'eventAt', event_at)
                ORDER BY event_at DESC),'[]'::jsonb)) INTO result
        FROM (SELECT * FROM private.admin_audit_events ORDER BY event_at DESC LIMIT 8) recent;
        RETURN result;
    END IF;

    IF action = 'users' THEN
        WITH rows AS (
            SELECT u.id, COALESCE(u.email,'') email,
                COALESCE(p.display_name,u.raw_user_meta_data->>'display_name',u.raw_user_meta_data->>'full_name') display_name,
                COALESCE(p.status::text,'active') status, u.created_at, u.last_sign_in_at,
                d.public_device_code, COALESCE(d.status::text,'none') device_status,
                COALESCE(w.available_credits,0) available_credits, COALESCE(w.reserved_credits,0) reserved_credits,
                CASE WHEN s.subscription_id IS NULL THEN 'none' WHEN s.status='active' AND s.expires_at<=clock_timestamp() THEN 'expired' ELSE s.status::text END subscription_status,
                s.expires_at subscription_expires_at
            FROM auth.users u
            LEFT JOIN private.managed_user_profiles p ON p.user_id=u.id
            LEFT JOIN private.device_profiles d ON d.auth_user_id=u.id
            LEFT JOIN private.credit_wallets w ON w.user_id=u.id
            LEFT JOIN private.subscriptions s ON s.device_profile_id=d.device_profile_id
            WHERE (search_query IS NULL OR u.id::text ILIKE '%'||search_query||'%' OR COALESCE(u.email,'') ILIKE '%'||search_query||'%' OR COALESCE(d.public_device_code,'') ILIKE '%'||upper(search_query)||'%')
              AND (status_filter IS NULL OR COALESCE(p.status::text,'active')=status_filter)
        ), page AS (SELECT * FROM rows ORDER BY created_at DESC,id DESC LIMIT page_limit OFFSET page_offset)
        SELECT jsonb_build_object('totalCount',(SELECT count(*) FROM rows),'items',COALESCE(jsonb_agg(jsonb_build_object(
            'userId',id,'email',email,'displayName',display_name,'status',status,'createdAt',created_at,
            'lastSignInAt',last_sign_in_at,'publicDeviceCode',public_device_code,'deviceStatus',device_status,
            'availableCredits',available_credits,'reservedCredits',reserved_credits,'subscriptionStatus',subscription_status,
            'subscriptionExpiresAt',subscription_expires_at) ORDER BY created_at DESC),'[]'::jsonb)) INTO result FROM page;
        RETURN result;
    END IF;

    IF action = 'user_update' THEN
        IF actor_role = 'auditor' THEN RAISE EXCEPTION 'ADMIN_MUTATION_FORBIDDEN' USING ERRCODE='42501'; END IF;
        target_id := (payload->>'userId')::uuid; correlation := (payload->>'correlationId')::uuid;
        reason := left(btrim(COALESCE(payload->>'reason','')),500);
        status_filter := payload->>'status';
        IF reason='' OR status_filter NOT IN ('active','suspended','deactivated') THEN RAISE EXCEPTION 'ADMIN_MUTATION_INVALID'; END IF;
        INSERT INTO private.managed_user_profiles(user_id,display_name,contact_email,status,internal_note)
        SELECT target_id,NULLIF(left(btrim(payload->>'displayName'),128),''),NULLIF(left(btrim(payload->>'contactEmail'),320),''),status_filter::private.managed_user_status,NULLIF(left(btrim(payload->>'internalNote'),1000),'')
        WHERE EXISTS(SELECT 1 FROM auth.users WHERE id=target_id)
        ON CONFLICT(user_id) DO UPDATE SET display_name=excluded.display_name,contact_email=excluded.contact_email,status=excluded.status,internal_note=excluded.internal_note,updated_at=clock_timestamp();
        IF status_filter IN ('suspended','deactivated') THEN
            UPDATE private.device_profiles SET status=(CASE WHEN status_filter='deactivated' THEN 'revoked' ELSE 'blocked' END)::private.device_commercial_status,updated_at=clock_timestamp() WHERE auth_user_id=target_id AND status<>'revoked';
        END IF;
        PERFORM private.admin_audit_record(actor_id,'USER_UPDATED','AUTH_USER',target_id,correlation,jsonb_build_object('status',status_filter,'reason',reason));
        RETURN jsonb_build_object('code','ADMIN_USER_UPDATED');
    END IF;

    IF action = 'devices' THEN
        WITH rows AS (
            SELECT dp.device_profile_id,dp.auth_user_id,dp.public_device_code,dp.status::text device_status,
                COALESCE(s.status::text,'none') subscription_status,s.starts_at,s.expires_at subscription_expires_at,
                COALESCE(w.available_credits,0) available_credits,COALESCE(w.reserved_credits,0) reserved_credits,
                dp.created_at,dp.last_seen_at
            FROM private.device_profiles dp LEFT JOIN private.subscriptions s USING(device_profile_id)
            LEFT JOIN private.credit_wallets w ON w.user_id=dp.auth_user_id
            WHERE (search_query IS NULL OR dp.public_device_code ILIKE '%'||upper(search_query)||'%' OR dp.auth_user_id::text ILIKE '%'||search_query||'%')
              AND (status_filter IS NULL OR dp.status::text=status_filter)
        ), page AS (SELECT * FROM rows ORDER BY last_seen_at DESC,device_profile_id DESC LIMIT page_limit OFFSET page_offset)
        SELECT jsonb_build_object('totalCount',(SELECT count(*) FROM rows),'items',COALESCE(jsonb_agg(jsonb_build_object(
            'deviceProfileId',device_profile_id,'authUserId',auth_user_id,'publicDeviceCode',public_device_code,
            'deviceStatus',device_status,'subscriptionStatus',subscription_status,'startsAt',starts_at,
            'subscriptionExpiresAt',subscription_expires_at,'availableCredits',available_credits,
            'reservedCredits',reserved_credits,'createdAt',created_at,'lastSeenAt',last_seen_at)
            ORDER BY last_seen_at DESC),'[]'::jsonb)) INTO result FROM page;
        RETURN result;
    END IF;

    IF action = 'device_status' THEN
        IF actor_role='auditor' THEN RAISE EXCEPTION 'ADMIN_MUTATION_FORBIDDEN' USING ERRCODE='42501'; END IF;
        target_id := (payload->>'deviceProfileId')::uuid; status_filter := payload->>'status';
        IF status_filter='block' THEN status_filter := 'blocked'; END IF;
        correlation := (payload->>'correlationId')::uuid; reason := left(btrim(COALESCE(payload->>'reason','')),500);
        IF reason='' OR status_filter NOT IN ('active','blocked','revoked') THEN RAISE EXCEPTION 'ADMIN_MUTATION_INVALID'; END IF;
        UPDATE private.device_profiles SET status=status_filter::private.device_commercial_status,updated_at=clock_timestamp() WHERE device_profile_id=target_id;
        IF status_filter='revoked' THEN UPDATE private.subscriptions SET status='revoked',updated_at=clock_timestamp() WHERE device_profile_id=target_id; END IF;
        PERFORM private.admin_audit_record(actor_id,CASE status_filter WHEN 'active' THEN 'DEVICE_UNBLOCKED' WHEN 'blocked' THEN 'DEVICE_BLOCKED' ELSE 'DEVICE_REVOKED' END,'DEVICE_PROFILE',target_id,correlation,jsonb_build_object('status',status_filter,'reason',reason));
        RETURN jsonb_build_object('code','ADMIN_DEVICE_STATUS_UPDATED');
    END IF;

    IF action = 'transactions' THEN
        WITH rows AS (
            SELECT e.*,COALESCE(u.email,'') email FROM private.payment_events e LEFT JOIN auth.users u ON u.id=e.user_id
            WHERE (search_query IS NULL OR e.payment_event_id::text ILIKE '%'||search_query||'%' OR e.provider_event_id ILIKE '%'||search_query||'%' OR e.provider_payment_id ILIKE '%'||search_query||'%' OR e.grant_transaction_id::text ILIKE '%'||search_query||'%' OR e.user_id::text ILIKE '%'||search_query||'%' OR COALESCE(u.email,'') ILIKE '%'||search_query||'%')
              AND (NULLIF(payload->>'provider','') IS NULL OR e.provider=payload->>'provider')
        ), page AS (SELECT * FROM rows ORDER BY verified_at DESC,payment_event_id DESC LIMIT page_limit OFFSET page_offset)
        SELECT jsonb_build_object('totalCount',(SELECT count(*) FROM rows),'items',COALESCE(jsonb_agg(jsonb_build_object(
            'paymentEventId',payment_event_id,'provider',provider,'providerEventId',provider_event_id,'providerPaymentId',provider_payment_id,
            'userId',user_id,'email',email,'productId',product_id,'amountMinor',amount_minor,'currency',currency::text,
            'credits',credits,'grantTransactionId',grant_transaction_id,'verifiedAt',verified_at) ORDER BY verified_at DESC),'[]'::jsonb)) INTO result FROM page;
        RETURN result;
    END IF;

    IF action = 'packages' THEN
        SELECT jsonb_build_object('items',COALESCE(jsonb_agg(jsonb_build_object(
            'packageId',package_id,'productId',product_id,'displayName',display_name,'description',description,
            'amountMinor',amount_minor,'currency',currency::text,'credits',credits,'isActive',is_active,
            'sortOrder',sort_order,'archivedAt',archived_at,'createdAt',created_at,'updatedAt',updated_at)
            ORDER BY archived_at NULLS FIRST,sort_order,created_at),'[]'::jsonb)) INTO result FROM private.commercial_packages;
        RETURN result;
    END IF;

    IF action = 'package_save' THEN
        IF actor_role='auditor' THEN RAISE EXCEPTION 'ADMIN_MUTATION_FORBIDDEN' USING ERRCODE='42501'; END IF;
        saved_package_id := COALESCE(NULLIF(payload->>'packageId','')::uuid,gen_random_uuid()); correlation := (payload->>'correlationId')::uuid;
        reason := left(btrim(COALESCE(payload->>'reason','')),500); IF reason='' THEN RAISE EXCEPTION 'ADMIN_MUTATION_INVALID'; END IF;
        INSERT INTO private.commercial_packages(package_id,product_id,display_name,description,amount_minor,currency,credits,is_active,sort_order,archived_at)
        VALUES(saved_package_id,payload->>'productId',payload->>'displayName',NULLIF(payload->>'description',''),(payload->>'amountMinor')::bigint,lower(payload->>'currency'),(payload->>'credits')::bigint,COALESCE((payload->>'isActive')::boolean,true),COALESCE((payload->>'sortOrder')::integer,0),CASE WHEN COALESCE((payload->>'isActive')::boolean,true) THEN NULL ELSE clock_timestamp() END)
        ON CONFLICT(package_id) DO UPDATE SET product_id=excluded.product_id,display_name=excluded.display_name,description=excluded.description,amount_minor=excluded.amount_minor,currency=excluded.currency,credits=excluded.credits,is_active=excluded.is_active,sort_order=excluded.sort_order,archived_at=CASE WHEN excluded.is_active THEN NULL ELSE COALESCE(private.commercial_packages.archived_at,clock_timestamp()) END,updated_at=clock_timestamp();
        PERFORM private.admin_audit_record(actor_id,'PACKAGE_SAVED','COMMERCIAL_PACKAGE',saved_package_id,correlation,jsonb_build_object('reason',reason));
        RETURN jsonb_build_object('code','ADMIN_PACKAGE_SAVED','packageId',saved_package_id);
    END IF;

    IF action = 'package_archive' THEN
        IF actor_role='auditor' THEN RAISE EXCEPTION 'ADMIN_MUTATION_FORBIDDEN' USING ERRCODE='42501'; END IF;
        target_id := (payload->>'packageId')::uuid; correlation := (payload->>'correlationId')::uuid; reason := left(btrim(COALESCE(payload->>'reason','')),500);
        IF reason='' THEN RAISE EXCEPTION 'ADMIN_MUTATION_INVALID'; END IF;
        UPDATE private.commercial_packages SET is_active=false,archived_at=COALESCE(archived_at,clock_timestamp()),updated_at=clock_timestamp() WHERE package_id=target_id;
        PERFORM private.admin_audit_record(actor_id,'PACKAGE_ARCHIVED','COMMERCIAL_PACKAGE',target_id,correlation,jsonb_build_object('reason',reason));
        RETURN jsonb_build_object('code','ADMIN_PACKAGE_ARCHIVED');
    END IF;

    IF action = 'gift_codes' THEN
        SELECT jsonb_build_object('totalCount',count(*),'items',COALESCE(jsonb_agg(jsonb_build_object(
            'giftCodeId',gift_code_id,'codePrefix',code_prefix,'kind',kind::text,'durationDays',duration_days,
            'creditAmount',credit_amount,'maximumRedemptions',maximum_redemptions,'redemptionCount',redemption_count,
            'expiresAt',expires_at,'disabledAt',disabled_at,'createdAt',created_at) ORDER BY created_at DESC),'[]'::jsonb))
        INTO result FROM (SELECT * FROM private.gift_codes ORDER BY created_at DESC LIMIT page_limit OFFSET page_offset) g;
        RETURN result;
    END IF;

    IF action = 'gift_create' THEN
        IF actor_role='auditor' THEN RAISE EXCEPTION 'ADMIN_MUTATION_FORBIDDEN' USING ERRCODE='42501'; END IF;
        correlation := (payload->>'correlationId')::uuid; reason := left(btrim(COALESCE(payload->>'reason','')),500);
        IF reason='' OR payload->>'kind' NOT IN ('duration','credits') THEN RAISE EXCEPTION 'ADMIN_MUTATION_INVALID'; END IF;
        generated_code := 'GFT-'||upper(substr(replace(gen_random_uuid()::text,'-',''),1,4))||'-'||upper(substr(replace(gen_random_uuid()::text,'-',''),1,4))||'-'||upper(substr(replace(gen_random_uuid()::text,'-',''),1,4))||'-'||upper(substr(replace(gen_random_uuid()::text,'-',''),1,4));
        INSERT INTO private.gift_codes(code_prefix,code_sha256,kind,duration_days,credit_amount,maximum_redemptions,expires_at)
        VALUES(left(generated_code,12),digest(upper(generated_code),'sha256'),(payload->>'kind')::private.gift_code_kind,
            CASE WHEN payload->>'kind'='duration' THEN (payload->>'durationDays')::integer END,
            CASE WHEN payload->>'kind'='credits' THEN (payload->>'creditAmount')::bigint END,
            (payload->>'maximumRedemptions')::integer,NULLIF(payload->>'expiresAt','')::timestamptz)
        RETURNING gift_code_id INTO target_id;
        PERFORM private.admin_audit_record(actor_id,'GIFT_CODE_CREATED','GIFT_CODE',target_id,correlation,jsonb_build_object('reason',reason,'codePrefix',left(generated_code,12)));
        RETURN jsonb_build_object('code','ADMIN_GIFT_CODE_CREATED','oneTimeCode',generated_code);
    END IF;

    IF action = 'gift_revoke' THEN
        IF actor_role='auditor' THEN RAISE EXCEPTION 'ADMIN_MUTATION_FORBIDDEN' USING ERRCODE='42501'; END IF;
        target_id := (payload->>'giftCodeId')::uuid; correlation := (payload->>'correlationId')::uuid; reason := left(btrim(COALESCE(payload->>'reason','')),500);
        IF reason='' THEN RAISE EXCEPTION 'ADMIN_MUTATION_INVALID'; END IF;
        UPDATE private.gift_codes SET disabled_at=COALESCE(disabled_at,clock_timestamp()) WHERE gift_code_id=target_id;
        PERFORM private.admin_audit_record(actor_id,'GIFT_CODE_REVOKED','GIFT_CODE',target_id,correlation,jsonb_build_object('reason',reason));
        RETURN jsonb_build_object('code','ADMIN_GIFT_CODE_REVOKED');
    END IF;

    IF action = 'admins' THEN
        IF actor_role<>'owner' THEN RAISE EXCEPTION 'ADMIN_OWNER_REQUIRED' USING ERRCODE='42501'; END IF;
        SELECT jsonb_build_object('items',COALESCE(jsonb_agg(jsonb_build_object(
            'adminUserId',a.admin_user_id,'email',COALESCE(u.email,''),'role',a.role::text,'mfaState',a.mfa_state::text,
            'displayLabel',a.display_label,'isActive',a.is_active,'createdAt',a.created_at,'lastSeenAt',a.last_seen_at)
            ORDER BY a.created_at),'[]'::jsonb)) INTO result FROM private.admin_users a LEFT JOIN auth.users u ON u.id=a.admin_user_id;
        RETURN result;
    END IF;

    IF action = 'admin_update' THEN
        IF actor_role<>'owner' THEN RAISE EXCEPTION 'ADMIN_OWNER_REQUIRED' USING ERRCODE='42501'; END IF;
        target_id := (payload->>'adminUserId')::uuid; correlation := (payload->>'correlationId')::uuid; reason := left(btrim(COALESCE(payload->>'reason','')),500);
        IF reason='' OR (target_id=actor_id AND NOT (payload->>'isActive')::boolean) THEN RAISE EXCEPTION 'ADMIN_MUTATION_INVALID'; END IF;
        IF (NOT (payload->>'isActive')::boolean OR payload->>'role'<>'owner')
           AND EXISTS(SELECT 1 FROM private.admin_users WHERE admin_user_id=target_id AND role='owner' AND is_active)
           AND NOT EXISTS(SELECT 1 FROM private.admin_users WHERE admin_user_id<>target_id AND role='owner' AND is_active)
        THEN RAISE EXCEPTION 'ADMIN_LAST_OWNER_REQUIRED'; END IF;
        UPDATE private.admin_users SET role=(payload->>'role')::private.admin_role,is_active=(payload->>'isActive')::boolean,mfa_state=(payload->>'mfaState')::private.admin_mfa_state,display_label=NULLIF(left(btrim(payload->>'displayLabel'),128),''),updated_at=clock_timestamp() WHERE admin_user_id=target_id;
        PERFORM private.admin_audit_record(actor_id,'ADMIN_ACCOUNT_UPDATED','ADMIN_USER',target_id,correlation,jsonb_build_object('reason',reason));
        RETURN jsonb_build_object('code','ADMIN_ACCOUNT_UPDATED');
    END IF;

    IF action = 'audit' THEN
        SELECT jsonb_build_object('items',COALESCE(jsonb_agg(jsonb_build_object(
            'eventId',event_id,'actorAdminUserId',actor_admin_user_id,'eventType',event_type,'targetKind',target_kind,
            'targetId',target_id,'correlationId',correlation_id,'details',details::text,'eventAt',event_at)
            ORDER BY event_at DESC),'[]'::jsonb)) INTO result
        FROM (SELECT * FROM private.admin_audit_events ORDER BY event_at DESC LIMIT page_limit OFFSET page_offset) e;
        RETURN result;
    END IF;

    RAISE EXCEPTION 'ADMIN_ACTION_UNKNOWN';
END;
$function$;

REVOKE ALL ON FUNCTION public.admin_portal_api(text,jsonb) FROM PUBLIC, anon;
GRANT EXECUTE ON FUNCTION public.admin_portal_api(text,jsonb) TO authenticated;

COMMIT;
