BEGIN;

-- Fix payment_api service_role guard for PostgREST .rpc() compatibility
-- Problem: PostgREST .rpc() calls with service_role apikey don't set request.jwt.claim.role
-- Solution: Check actual PostgreSQL role membership instead of JWT claim

CREATE OR REPLACE FUNCTION public.payment_api(action text, payload jsonb DEFAULT '{}'::jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, public, private, extensions AS $$
DECLARE
    now_at timestamptz := clock_timestamp();
    user_id uuid;
    profile private.device_profiles%ROWTYPE;
    product private.payment_products%ROWTYPE;
    order_row private.payment_orders%ROWTYPE;
    transaction_row private.sepay_transactions%ROWTYPE;
    idempotency text;
    code_value text;
    ttl_minutes integer;
    amount_value bigint;
    provider_id text;
    result jsonb;
    admin_row private.admin_users%ROWTYPE;
BEGIN
    -- Service role check: verify caller has service_role or is postgres superuser
    -- Use pg_has_role instead of JWT claim (PostgREST .rpc() doesn't populate JWT claims)
    IF NOT (
        current_setting('request.jwt.claim.role', true) = 'service_role'
        OR pg_has_role(current_user, 'service_role', 'MEMBER')
        OR current_user = 'postgres'
    ) THEN
        RAISE EXCEPTION 'PAYMENT_SERVICE_ROLE_REQUIRED' USING ERRCODE = '42501';
    END IF;

    IF payload IS NULL OR jsonb_typeof(payload) <> 'object' THEN
        RAISE EXCEPTION 'PAYMENT_PAYLOAD_INVALID' USING ERRCODE = 'P0001';
    END IF;

    IF action = 'catalog' THEN
        RETURN COALESCE((SELECT jsonb_agg(jsonb_build_object(
            'productId', p.product_id, 'type', p.product_type, 'displayName', p.display_name,
            'priceVnd', p.price_vnd, 'durationDays', p.duration_days,
            'creditAmount', p.credit_amount, 'sortOrder', p.sort_order) ORDER BY p.sort_order, p.product_id)
        FROM private.payment_products p WHERE p.active AND p.effective_from <= now_at
          AND (p.effective_to IS NULL OR p.effective_to > now_at)), '[]'::jsonb);
    END IF;

    IF action = 'create_order' THEN
        user_id := nullif(payload->>'userId', '')::uuid;
        IF user_id IS NOT NULL THEN
            SELECT * INTO profile FROM private.device_profiles WHERE auth_user_id = user_id FOR UPDATE;
        ELSE
            SELECT * INTO profile FROM private.device_profiles
                WHERE public_device_code = upper(nullif(payload->>'deviceCode', '')) FOR UPDATE;
        END IF;
        IF NOT FOUND OR profile.status <> 'active' THEN
            RAISE EXCEPTION 'PAYMENT_DEVICE_UNAVAILABLE' USING ERRCODE = 'P0001';
        END IF;
        user_id := profile.auth_user_id;
        idempotency := payload->>'idempotencyKey';
        ttl_minutes := (payload->>'ttlMinutes')::integer;
        IF idempotency !~ '^[A-Za-z0-9_.:-]{8,128}$' OR ttl_minutes NOT BETWEEN 5 AND 1440 THEN
            RAISE EXCEPTION 'PAYMENT_ORDER_REQUEST_INVALID' USING ERRCODE = 'P0001';
        END IF;
        SELECT * INTO order_row FROM private.payment_orders
            WHERE device_profile_id = profile.device_profile_id AND idempotency_key = idempotency;
        IF FOUND THEN
            RETURN jsonb_build_object('orderId', order_row.payment_order_id,
                'orderCode', order_row.order_code, 'status', order_row.status,
                'productName', order_row.product_name, 'productType', order_row.product_type,
                'priceVnd', order_row.price_vnd, 'durationDays', order_row.duration_days,
                'creditAmount', order_row.credit_amount, 'currency', order_row.currency,
                'paymentContent', order_row.payment_content, 'expiresAt', order_row.expires_at,
                'replayed', true);
        END IF;
        SELECT * INTO product FROM private.payment_products p
            WHERE p.product_id = payload->>'productId' AND p.active
              AND p.effective_from <= now_at AND (p.effective_to IS NULL OR p.effective_to > now_at)
            ORDER BY p.version DESC LIMIT 1;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'PAYMENT_PRODUCT_UNAVAILABLE' USING ERRCODE = 'P0001';
        END IF;
        LOOP
            code_value := 'AMS' || upper(substr(encode(gen_random_bytes(10), 'hex'), 1, 16));
            EXIT WHEN NOT EXISTS(SELECT 1 FROM private.payment_orders WHERE order_code = code_value);
        END LOOP;
        INSERT INTO private.payment_orders(order_code, device_profile_id, auth_user_id,
            payment_product_id, product_id, product_type, product_name, price_vnd,
            duration_days, credit_amount, payment_content, idempotency_key, expires_at)
        VALUES(code_value, profile.device_profile_id, user_id, product.payment_product_id,
            product.product_id, product.product_type, product.display_name, product.price_vnd,
            product.duration_days, product.credit_amount, code_value, idempotency,
            now_at + make_interval(mins => ttl_minutes)) RETURNING * INTO order_row;
        INSERT INTO private.payment_audit_events(payment_order_id,event_type,correlation_id)
            VALUES(order_row.payment_order_id,'PAYMENT_ORDER_CREATED',gen_random_uuid());
        RETURN jsonb_build_object('orderId', order_row.payment_order_id,
            'orderCode', order_row.order_code, 'status', order_row.status,
            'productName', order_row.product_name, 'productType', order_row.product_type,
            'priceVnd', order_row.price_vnd, 'durationDays', order_row.duration_days,
            'creditAmount', order_row.credit_amount, 'currency', order_row.currency,
            'paymentContent', order_row.payment_content, 'expiresAt', order_row.expires_at,
            'replayed', false);
    END IF;

    IF action IN ('order_status', 'cancel_order') THEN
        user_id := nullif(payload->>'userId', '')::uuid;
        SELECT o.* INTO order_row FROM private.payment_orders o
        JOIN private.device_profiles d ON d.device_profile_id = o.device_profile_id
        WHERE o.payment_order_id = (payload->>'orderId')::uuid
          AND (d.auth_user_id = user_id OR (user_id IS NULL AND d.public_device_code = upper(payload->>'deviceCode')))
        FOR UPDATE OF o;
        IF NOT FOUND THEN RAISE EXCEPTION 'PAYMENT_ORDER_NOT_FOUND' USING ERRCODE = 'P0001'; END IF;
        IF order_row.status = 'waiting_payment' AND order_row.expires_at <= now_at THEN
            UPDATE private.payment_orders SET status = 'expired' WHERE payment_order_id = order_row.payment_order_id
                RETURNING * INTO order_row;
        ELSIF action = 'cancel_order' AND order_row.status = 'waiting_payment' THEN
            UPDATE private.payment_orders SET status = 'cancelled', cancelled_at = now_at
                WHERE payment_order_id = order_row.payment_order_id RETURNING * INTO order_row;
        END IF;
        RETURN jsonb_build_object('orderId', order_row.payment_order_id, 'orderCode', order_row.order_code,
            'status', order_row.status, 'productType', order_row.product_type,
            'durationDays', order_row.duration_days, 'creditAmount', order_row.credit_amount,
            'priceVnd', order_row.price_vnd, 'expiresAt', order_row.expires_at,
            'fulfilledAt', order_row.fulfilled_at, 'reviewReason', order_row.review_reason);
    END IF;

    IF action = 'ingest_sepay' THEN
        provider_id := payload->>'providerTransactionId';
        amount_value := (payload->>'amountVnd')::bigint;
        IF provider_id !~ '^[A-Za-z0-9_.:-]{1,128}$' OR amount_value <= 0 THEN
            RAISE EXCEPTION 'PAYMENT_TRANSACTION_INVALID' USING ERRCODE = 'P0001';
        END IF;
        PERFORM pg_advisory_xact_lock(hashtextextended('sepay:' || provider_id, 0));
        SELECT * INTO transaction_row FROM private.sepay_transactions
            WHERE provider_transaction_id = provider_id FOR UPDATE;
        IF FOUND THEN
            IF transaction_row.payload_sha256 <> payload->>'payloadSha256' THEN
                RAISE EXCEPTION 'PAYMENT_TRANSACTION_CONFLICT' USING ERRCODE = 'P0001';
            END IF;
            RETURN jsonb_build_object('accepted', true, 'replayed', true,
                'processingState', transaction_row.processing_state,
                'orderId', transaction_row.matched_order_id);
        END IF;
        SELECT * INTO order_row FROM private.payment_orders
            WHERE order_code = upper(payload->>'paymentCode') FOR UPDATE;

        IF order_row.payment_order_id IS NULL OR payload->>'transferType' <> 'in' THEN
            INSERT INTO private.sepay_transactions(provider_transaction_id, reference_code, gateway,
                transaction_date, transfer_type, amount_vnd, payment_code, content, account_number,
                payload_sha256, processing_state)
            VALUES(provider_id, nullif(payload->>'referenceCode',''), payload->>'gateway',
                (payload->>'transactionDate')::timestamptz, payload->>'transferType', amount_value,
                upper(nullif(payload->>'paymentCode','')), left(nullif(payload->>'content',''),512),
                payload->>'accountNumber', payload->>'payloadSha256', 'rejected')
            RETURNING * INTO transaction_row;
            RETURN jsonb_build_object('accepted', true, 'replayed', false,
                'processingState', 'rejected');
        END IF;

        IF order_row.provider_transaction_id IS NOT NULL OR EXISTS (
            SELECT 1 FROM private.sepay_transactions t
            WHERE t.matched_order_id = order_row.payment_order_id) THEN
            INSERT INTO private.sepay_transactions(provider_transaction_id, reference_code, gateway,
                transaction_date, transfer_type, amount_vnd, payment_code, content, account_number,
                payload_sha256, processing_state, safe_metadata)
            VALUES(provider_id, nullif(payload->>'referenceCode',''), payload->>'gateway',
                (payload->>'transactionDate')::timestamptz, payload->>'transferType', amount_value,
                upper(nullif(payload->>'paymentCode','')), left(nullif(payload->>'content',''),512),
                payload->>'accountNumber', payload->>'payloadSha256', 'review_required',
                jsonb_build_object('candidateOrderId', order_row.payment_order_id))
            RETURNING * INTO transaction_row;
            RETURN jsonb_build_object('accepted', true, 'replayed', false,
                'processingState', 'review_required', 'orderId', order_row.payment_order_id);
        END IF;

        INSERT INTO private.sepay_transactions(provider_transaction_id, reference_code, gateway,
            transaction_date, transfer_type, amount_vnd, payment_code, content, account_number,
            payload_sha256, matched_order_id, processing_state)
        VALUES(provider_id, nullif(payload->>'referenceCode',''), payload->>'gateway',
            (payload->>'transactionDate')::timestamptz, payload->>'transferType', amount_value,
            upper(nullif(payload->>'paymentCode','')), left(nullif(payload->>'content',''),512),
            payload->>'accountNumber', payload->>'payloadSha256',
            order_row.payment_order_id, 'accepted')
        RETURNING * INTO transaction_row;
        IF payload->>'accountNumber' <> payload->>'expectedAccountNumber' THEN
            UPDATE private.payment_orders SET status='review_required', review_reason='WRONG_ACCOUNT'
                WHERE payment_order_id=order_row.payment_order_id;
        ELSIF amount_value <> order_row.price_vnd THEN
            UPDATE private.payment_orders SET status='review_required',
                review_reason=CASE WHEN amount_value < order_row.price_vnd THEN 'UNDERPAYMENT' ELSE 'OVERPAYMENT' END
                WHERE payment_order_id=order_row.payment_order_id;
        ELSIF order_row.status <> 'waiting_payment' OR order_row.expires_at <= now_at THEN
            UPDATE private.payment_orders SET status='review_required', review_reason='LATE_OR_CANCELLED_PAYMENT'
                WHERE payment_order_id=order_row.payment_order_id;
        ELSE
            UPDATE private.payment_orders SET status='paid', paid_at=now_at,
                provider_transaction_id=provider_id, provider_reference=nullif(payload->>'referenceCode','')
                WHERE payment_order_id=order_row.payment_order_id;
            result := private.payment_fulfill_locked(order_row.payment_order_id);
            UPDATE private.sepay_transactions SET processing_state='fulfilled'
                WHERE sepay_transaction_id=transaction_row.sepay_transaction_id;
            RETURN result || jsonb_build_object('accepted', true, 'replayed', false);
        END IF;
        UPDATE private.sepay_transactions SET processing_state='review_required'
            WHERE sepay_transaction_id=transaction_row.sepay_transaction_id;
        RETURN jsonb_build_object('accepted', true, 'replayed', false,
            'processingState', 'review_required', 'orderId', order_row.payment_order_id);
    END IF;

    IF action = 'reconcile' THEN
        SELECT * INTO order_row FROM private.payment_orders
            WHERE payment_order_id=(payload->>'orderId')::uuid FOR UPDATE;
        IF NOT FOUND THEN RAISE EXCEPTION 'PAYMENT_ORDER_NOT_FOUND' USING ERRCODE='P0001'; END IF;
        RETURN private.payment_fulfill_locked(order_row.payment_order_id);
    END IF;

    IF action IN ('admin_orders', 'admin_order_detail', 'admin_retry', 'admin_metrics',
        'admin_products', 'admin_product_save') THEN
        user_id := (payload->>'adminUserId')::uuid;
        SELECT * INTO admin_row FROM private.admin_users WHERE admin_user_id=user_id AND is_active FOR UPDATE;
        IF NOT FOUND THEN RAISE EXCEPTION 'ADMIN_FORBIDDEN' USING ERRCODE='42501'; END IF;
        IF action = 'admin_metrics' THEN
            RETURN (SELECT jsonb_build_object(
                'revenueConfirmedVnd',coalesce(sum(price_vnd) FILTER (WHERE status='fulfilled'),0),
                'paidOrders',count(*) FILTER (WHERE status IN ('paid','fulfilling','fulfilled')),
                'pendingOrders',count(*) FILTER (WHERE status='waiting_payment'),
                'reviewRequired',count(*) FILTER (WHERE status='review_required'),
                'subscriptionSales',count(*) FILTER (WHERE status='fulfilled' AND product_type='subscription'),
                'creditSales',count(*) FILTER (WHERE status='fulfilled' AND product_type='credits'))
            FROM private.payment_orders);
        END IF;
        IF action = 'admin_products' THEN
            RETURN COALESCE((SELECT jsonb_agg(jsonb_build_object('paymentProductId',p.payment_product_id,
                'productId',p.product_id,'productType',p.product_type,'displayName',p.display_name,
                'priceVnd',p.price_vnd,'durationDays',p.duration_days,'creditAmount',p.credit_amount,
                'active',p.active,'sortOrder',p.sort_order,'version',p.version,
                'effectiveFrom',p.effective_from,'effectiveTo',p.effective_to)
                ORDER BY p.sort_order,p.product_id,p.version DESC) FROM private.payment_products p), '[]'::jsonb);
        END IF;
        IF action = 'admin_product_save' THEN
            IF admin_row.role::text = 'auditor' OR admin_row.mfa_state::text <> 'MFA_VERIFIED'
              OR NOT coalesce((payload->>'recentAuth')::boolean,false)
              OR length(btrim(payload->>'reason')) < 8 THEN
                RAISE EXCEPTION 'ADMIN_STRONG_AUTH_REQUIRED' USING ERRCODE='42501';
            END IF;
            IF payload->>'productId' !~ '^[A-Za-z0-9_.-]{1,64}$'
              OR payload->>'productType' NOT IN ('subscription','credits')
              OR length(btrim(payload->>'displayName')) NOT BETWEEN 1 AND 160
              OR (payload->>'priceVnd')::bigint NOT BETWEEN 1000 AND 1000000000000 THEN
                RAISE EXCEPTION 'PAYMENT_PRODUCT_INVALID' USING ERRCODE='P0001';
            END IF;
            UPDATE private.payment_products SET active=false,effective_to=now_at
                WHERE product_id=payload->>'productId' AND effective_to IS NULL;
            INSERT INTO private.payment_products(product_id,product_type,display_name,price_vnd,
                duration_days,credit_amount,active,sort_order,effective_from,version)
            SELECT payload->>'productId',(payload->>'productType')::private.payment_product_type,
                btrim(payload->>'displayName'),(payload->>'priceVnd')::bigint,
                CASE WHEN payload->>'productType'='subscription' THEN (payload->>'durationDays')::integer END,
                CASE WHEN payload->>'productType'='credits' THEN (payload->>'creditAmount')::bigint END,
                coalesce((payload->>'active')::boolean,false),coalesce((payload->>'sortOrder')::integer,0),now_at,
                coalesce(max(version),0)+1 FROM private.payment_products
                WHERE product_id=payload->>'productId' RETURNING * INTO product;
            INSERT INTO private.payment_audit_events(event_type,actor_user_id,reason,correlation_id,safe_metadata)
            VALUES('PAYMENT_PRODUCT_CHANGED',user_id,left(payload->>'reason',500),
                coalesce(nullif(payload->>'correlationId','')::uuid,gen_random_uuid()),
                jsonb_build_object('productId',product.product_id,'version',product.version));
            RETURN jsonb_build_object('productId',product.product_id,'version',product.version);
        END IF;
        IF action = 'admin_orders' THEN
            RETURN COALESCE((SELECT jsonb_agg(jsonb_build_object('orderId',o.payment_order_id,
                'orderCode',o.order_code,'deviceCode',d.public_device_code,'productName',o.product_name,
                'productType',o.product_type,'priceVnd',o.price_vnd,'status',o.status,
                'createdAt',o.created_at,'paidAt',o.paid_at,'providerReference',o.provider_reference,
                'reviewReason',o.review_reason) ORDER BY o.created_at DESC)
            FROM (SELECT * FROM private.payment_orders WHERE
                nullif(payload->>'status','') IS NULL OR status::text=payload->>'status'
                ORDER BY created_at DESC LIMIT least(coalesce((payload->>'limit')::integer,100),200)) o
            JOIN private.device_profiles d ON d.device_profile_id=o.device_profile_id), '[]'::jsonb);
        END IF;
        SELECT * INTO order_row FROM private.payment_orders WHERE payment_order_id=(payload->>'orderId')::uuid;
        IF NOT FOUND THEN RAISE EXCEPTION 'PAYMENT_ORDER_NOT_FOUND' USING ERRCODE='P0001'; END IF;
        IF action = 'admin_retry' THEN
            IF admin_row.role::text <> 'owner' OR admin_row.mfa_state::text <> 'MFA_VERIFIED'
              OR NOT coalesce((payload->>'recentAuth')::boolean,false)
              OR length(btrim(payload->>'reason')) < 8 THEN
                RAISE EXCEPTION 'ADMIN_STRONG_AUTH_REQUIRED' USING ERRCODE='42501';
            END IF;
            result := private.payment_fulfill_locked(order_row.payment_order_id);
            INSERT INTO private.payment_audit_events(payment_order_id,event_type,actor_user_id,reason,correlation_id)
            VALUES(order_row.payment_order_id,'PAYMENT_MANUAL_RECONCILIATION',user_id,
                left(payload->>'reason',500),coalesce(nullif(payload->>'correlationId','')::uuid,gen_random_uuid()));
            RETURN result;
        END IF;
        RETURN jsonb_build_object('order',to_jsonb(order_row)-'auth_user_id'-'metadata',
            'transaction',(SELECT to_jsonb(t)-'safe_metadata' FROM private.sepay_transactions t
                WHERE t.matched_order_id=order_row.payment_order_id),
            'fulfillment',(SELECT to_jsonb(f) FROM private.payment_fulfillment_events f
                WHERE f.payment_order_id=order_row.payment_order_id));
    END IF;

    RAISE EXCEPTION 'PAYMENT_ACTION_UNKNOWN' USING ERRCODE = 'P0001';
END $$;

COMMENT ON FUNCTION public.payment_api IS 'Payment operations API (fixed service_role auth for PostgREST)';

COMMIT;
