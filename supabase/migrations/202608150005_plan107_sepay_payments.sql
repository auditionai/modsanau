BEGIN;

CREATE TYPE private.payment_product_type AS ENUM ('subscription', 'credits');
CREATE TYPE private.payment_order_status AS ENUM (
    'waiting_payment', 'paid', 'fulfilling', 'fulfilled', 'expired',
    'cancelled', 'review_required', 'refunded');
CREATE TYPE private.payment_processing_state AS ENUM (
    'accepted', 'matched', 'fulfilled', 'review_required', 'rejected');

CREATE TABLE private.payment_products (
    payment_product_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id varchar(64) NOT NULL,
    product_type private.payment_product_type NOT NULL,
    display_name varchar(160) NOT NULL CHECK (length(btrim(display_name)) BETWEEN 1 AND 160),
    price_vnd bigint NOT NULL CHECK (price_vnd BETWEEN 1000 AND 1000000000000),
    duration_days integer,
    credit_amount bigint,
    active boolean NOT NULL DEFAULT false,
    sort_order integer NOT NULL DEFAULT 0,
    effective_from timestamptz NOT NULL DEFAULT clock_timestamp(),
    effective_to timestamptz,
    version integer NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK (product_id ~ '^[A-Za-z0-9_.-]{1,64}$'),
    CHECK ((product_type = 'subscription' AND duration_days BETWEEN 1 AND 3650 AND credit_amount IS NULL)
        OR (product_type = 'credits' AND credit_amount BETWEEN 1 AND 1000000000 AND duration_days IS NULL)),
    CHECK (effective_to IS NULL OR effective_to > effective_from),
    UNIQUE (product_id, version)
);

CREATE UNIQUE INDEX payment_products_one_current_version_idx
    ON private.payment_products(product_id) WHERE effective_to IS NULL;
CREATE INDEX payment_products_public_catalog_idx
    ON private.payment_products(active, sort_order, effective_from, effective_to);

CREATE TABLE private.payment_orders (
    payment_order_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    order_code varchar(24) NOT NULL UNIQUE CHECK (order_code ~ '^AMS[A-Z0-9]{12,20}$'),
    device_profile_id uuid NOT NULL REFERENCES private.device_profiles(device_profile_id),
    auth_user_id uuid NOT NULL,
    payment_product_id uuid NOT NULL REFERENCES private.payment_products(payment_product_id),
    product_id varchar(64) NOT NULL,
    product_type private.payment_product_type NOT NULL,
    product_name varchar(160) NOT NULL,
    price_vnd bigint NOT NULL CHECK (price_vnd BETWEEN 1000 AND 1000000000000),
    duration_days integer,
    credit_amount bigint,
    currency char(3) NOT NULL DEFAULT 'VND' CHECK (currency = 'VND'),
    status private.payment_order_status NOT NULL DEFAULT 'waiting_payment',
    payment_content varchar(64) NOT NULL,
    idempotency_key varchar(128) NOT NULL CHECK (idempotency_key ~ '^[A-Za-z0-9_.:-]{8,128}$'),
    provider varchar(32) NOT NULL DEFAULT 'sepay' CHECK (provider = 'sepay'),
    provider_transaction_id varchar(128) UNIQUE,
    provider_reference varchar(255),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    expires_at timestamptz NOT NULL,
    paid_at timestamptz,
    fulfilled_at timestamptz,
    cancelled_at timestamptz,
    review_reason varchar(64),
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(metadata) = 'object'),
    CHECK (expires_at > created_at),
    CHECK ((product_type = 'subscription' AND duration_days BETWEEN 1 AND 3650 AND credit_amount IS NULL)
        OR (product_type = 'credits' AND credit_amount BETWEEN 1 AND 1000000000 AND duration_days IS NULL)),
    UNIQUE (device_profile_id, idempotency_key)
);

CREATE INDEX payment_orders_device_time_idx
    ON private.payment_orders(device_profile_id, created_at DESC);
CREATE INDEX payment_orders_status_time_idx
    ON private.payment_orders(status, created_at);

CREATE TABLE private.sepay_transactions (
    sepay_transaction_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    provider_transaction_id varchar(128) NOT NULL UNIQUE CHECK (provider_transaction_id ~ '^[A-Za-z0-9_.:-]{1,128}$'),
    reference_code varchar(255),
    gateway varchar(100) NOT NULL,
    transaction_date timestamptz NOT NULL,
    transfer_type varchar(3) NOT NULL CHECK (transfer_type IN ('in', 'out')),
    amount_vnd bigint NOT NULL CHECK (amount_vnd > 0),
    payment_code varchar(64),
    content varchar(512),
    account_number varchar(64) NOT NULL,
    payload_sha256 char(64) NOT NULL CHECK (payload_sha256 ~ '^[0-9A-F]{64}$'),
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    matched_order_id uuid UNIQUE REFERENCES private.payment_orders(payment_order_id),
    processing_state private.payment_processing_state NOT NULL,
    safe_metadata jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(safe_metadata) = 'object')
);

CREATE TABLE private.payment_fulfillment_events (
    fulfillment_event_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    payment_order_id uuid NOT NULL UNIQUE REFERENCES private.payment_orders(payment_order_id),
    device_profile_id uuid NOT NULL REFERENCES private.device_profiles(device_profile_id),
    fulfillment_type private.payment_product_type NOT NULL,
    authority_reference varchar(128) NOT NULL UNIQUE,
    credit_transaction_id uuid UNIQUE REFERENCES private.credit_ledger(transaction_id),
    subscription_expires_at timestamptz,
    fulfilled_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK ((fulfillment_type = 'credits' AND credit_transaction_id IS NOT NULL AND subscription_expires_at IS NULL)
        OR (fulfillment_type = 'subscription' AND credit_transaction_id IS NULL AND subscription_expires_at IS NOT NULL))
);

CREATE TABLE private.payment_audit_events (
    audit_event_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    payment_order_id uuid REFERENCES private.payment_orders(payment_order_id),
    event_type varchar(64) NOT NULL CHECK (event_type ~ '^[A-Z0-9_]{1,64}$'),
    actor_user_id uuid,
    reason varchar(500),
    correlation_id uuid NOT NULL UNIQUE,
    safe_metadata jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(safe_metadata) = 'object'),
    occurred_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE OR REPLACE FUNCTION private.reject_payment_append_only_mutation()
RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, private AS $$
BEGIN
    RAISE EXCEPTION 'payment record is append-only' USING ERRCODE = '55000';
END $$;

CREATE TRIGGER payment_fulfillment_events_append_only
BEFORE UPDATE OR DELETE ON private.payment_fulfillment_events
FOR EACH ROW EXECUTE FUNCTION private.reject_payment_append_only_mutation();
CREATE TRIGGER payment_audit_events_append_only
BEFORE UPDATE OR DELETE ON private.payment_audit_events
FOR EACH ROW EXECUTE FUNCTION private.reject_payment_append_only_mutation();

CREATE OR REPLACE FUNCTION private.guard_payment_order_update()
RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, private AS $$
BEGIN
    IF (OLD.device_profile_id, OLD.auth_user_id, OLD.payment_product_id, OLD.product_id,
        OLD.product_type, OLD.product_name, OLD.price_vnd, OLD.duration_days, OLD.credit_amount,
        OLD.currency, OLD.payment_content, OLD.idempotency_key, OLD.provider, OLD.created_at,
        OLD.expires_at) IS DISTINCT FROM
       (NEW.device_profile_id, NEW.auth_user_id, NEW.payment_product_id, NEW.product_id,
        NEW.product_type, NEW.product_name, NEW.price_vnd, NEW.duration_days, NEW.credit_amount,
        NEW.currency, NEW.payment_content, NEW.idempotency_key, NEW.provider, NEW.created_at,
        NEW.expires_at) THEN
        RAISE EXCEPTION 'payment order snapshot is immutable' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER payment_orders_snapshot_immutable
BEFORE UPDATE ON private.payment_orders
FOR EACH ROW EXECUTE FUNCTION private.guard_payment_order_update();

CREATE OR REPLACE FUNCTION private.payment_fulfill_locked(p_order_id uuid)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, private, extensions AS $$
DECLARE
    order_row private.payment_orders%ROWTYPE;
    grant_status text;
    grant_transaction uuid;
    new_expiry timestamptz;
    request_hash text;
    authority text;
BEGIN
    SELECT * INTO order_row FROM private.payment_orders
        WHERE payment_order_id = p_order_id FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'PAYMENT_ORDER_NOT_FOUND' USING ERRCODE = 'P0001';
    END IF;
    IF order_row.status = 'fulfilled' THEN
        RETURN jsonb_build_object('status', 'fulfilled', 'replayed', true,
            'orderId', order_row.payment_order_id, 'productType', order_row.product_type);
    END IF;
    IF order_row.status NOT IN ('paid', 'fulfilling') THEN
        RAISE EXCEPTION 'PAYMENT_ORDER_NOT_PAID' USING ERRCODE = 'P0001';
    END IF;

    UPDATE private.payment_orders SET status = 'fulfilling'
        WHERE payment_order_id = order_row.payment_order_id;
    authority := 'SEPAY_ORDER:' || order_row.payment_order_id::text;

    IF order_row.product_type = 'credits' THEN
        request_hash := upper(encode(digest(order_row.payment_order_id::text || ':' ||
            order_row.credit_amount::text, 'sha256'), 'hex'));
        SELECT g.status_code, g.transaction_id INTO grant_status, grant_transaction
        FROM private.credit_grant(order_row.auth_user_id, order_row.credit_amount,
            authority, 'sepay.' || order_row.payment_order_id::text, request_hash) AS g;
        IF grant_status NOT IN ('CREDIT_APPLIED', 'CREDIT_IDEMPOTENT_REPLAY') THEN
            RAISE EXCEPTION 'PAYMENT_CREDIT_GRANT_FAILED' USING ERRCODE = 'P0001';
        END IF;
        INSERT INTO private.payment_fulfillment_events(payment_order_id, device_profile_id,
            fulfillment_type, authority_reference, credit_transaction_id)
        VALUES(order_row.payment_order_id, order_row.device_profile_id, 'credits', authority,
            grant_transaction) ON CONFLICT(payment_order_id) DO NOTHING;
    ELSE
        INSERT INTO private.subscriptions(device_profile_id, status, starts_at, expires_at)
        VALUES(order_row.device_profile_id, 'active', clock_timestamp(),
            clock_timestamp() + make_interval(days => order_row.duration_days))
        ON CONFLICT(device_profile_id) DO UPDATE SET status = 'active',
            expires_at = greatest(private.subscriptions.expires_at, clock_timestamp())
                + make_interval(days => order_row.duration_days),
            updated_at = clock_timestamp()
        RETURNING expires_at INTO new_expiry;
        INSERT INTO private.payment_fulfillment_events(payment_order_id, device_profile_id,
            fulfillment_type, authority_reference, subscription_expires_at)
        VALUES(order_row.payment_order_id, order_row.device_profile_id, 'subscription', authority,
            new_expiry) ON CONFLICT(payment_order_id) DO NOTHING;
    END IF;

    UPDATE private.payment_orders SET status = 'fulfilled', fulfilled_at = clock_timestamp()
        WHERE payment_order_id = order_row.payment_order_id;
    INSERT INTO private.device_commercial_events(device_profile_id, event_type, correlation_id, metadata)
    VALUES(order_row.device_profile_id,
        CASE WHEN order_row.product_type = 'credits' THEN 'PAYMENT_CREDITS_GRANTED'
             ELSE 'PAYMENT_SUBSCRIPTION_EXTENDED' END,
        order_row.payment_order_id,
        jsonb_build_object('orderId', order_row.payment_order_id, 'productId', order_row.product_id))
    ON CONFLICT(correlation_id) DO NOTHING;
    RETURN jsonb_build_object('status', 'fulfilled', 'replayed', false,
        'orderId', order_row.payment_order_id, 'productType', order_row.product_type,
        'creditAmount', order_row.credit_amount, 'durationDays', order_row.duration_days,
        'subscriptionExpiresAt', new_expiry);
END $$;

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
    IF current_setting('request.jwt.claim.role', true) IS DISTINCT FROM 'service_role' THEN
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

ALTER TABLE private.payment_products ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.payment_products FORCE ROW LEVEL SECURITY;
ALTER TABLE private.payment_orders ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.payment_orders FORCE ROW LEVEL SECURITY;
ALTER TABLE private.sepay_transactions ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.sepay_transactions FORCE ROW LEVEL SECURITY;
ALTER TABLE private.payment_fulfillment_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.payment_fulfillment_events FORCE ROW LEVEL SECURITY;
ALTER TABLE private.payment_audit_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.payment_audit_events FORCE ROW LEVEL SECURITY;

REVOKE ALL ON private.payment_products, private.payment_orders, private.sepay_transactions,
    private.payment_fulfillment_events, private.payment_audit_events FROM PUBLIC, anon, authenticated;
GRANT SELECT, INSERT, UPDATE ON private.payment_products, private.payment_orders,
    private.sepay_transactions, private.payment_fulfillment_events, private.payment_audit_events TO service_role;
REVOKE ALL ON FUNCTION public.payment_api(text,jsonb) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.payment_api(text,jsonb) TO service_role;

COMMIT;
