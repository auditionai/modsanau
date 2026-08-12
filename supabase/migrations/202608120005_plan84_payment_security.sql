BEGIN;

CREATE TABLE private.payment_events (
    payment_event_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    provider varchar(32) NOT NULL CHECK (provider ~ '^[a-z0-9]{1,32}$'),
    provider_event_id varchar(128) NOT NULL CHECK (provider_event_id ~ '^[A-Za-z0-9_.:-]{1,128}$'),
    provider_payment_id varchar(120) NOT NULL CHECK (provider_payment_id ~ '^[A-Za-z0-9_.:-]{1,120}$'),
    user_id uuid NOT NULL CHECK (user_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    product_id varchar(64) NOT NULL CHECK (product_id ~ '^[A-Za-z0-9_.-]{1,64}$'),
    amount_minor bigint NOT NULL CHECK (amount_minor > 0 AND amount_minor <= 1000000000000),
    currency char(3) NOT NULL CHECK (currency ~ '^[a-z]{3}$'),
    credits bigint NOT NULL CHECK (credits > 0 AND credits <= 1000000000),
    payload_sha256 char(64) NOT NULL CHECK (payload_sha256 ~ '^[0-9A-F]{64}$'),
    grant_request_sha256 char(64) NOT NULL CHECK (grant_request_sha256 ~ '^[0-9A-F]{64}$'),
    grant_transaction_id uuid NOT NULL UNIQUE REFERENCES private.credit_ledger(transaction_id),
    verified_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT payment_events_provider_event_unique UNIQUE (provider, provider_event_id),
    CONSTRAINT payment_events_provider_payment_unique UNIQUE (provider, provider_payment_id)
);

CREATE TRIGGER payment_events_append_only
BEFORE UPDATE OR DELETE ON private.payment_events
FOR EACH ROW EXECUTE FUNCTION private.reject_credit_immutable_mutation();

CREATE OR REPLACE FUNCTION private.payment_apply_verified(
    p_provider text,
    p_event_id text,
    p_payment_id text,
    p_user_id uuid,
    p_product_id text,
    p_amount_minor bigint,
    p_currency text,
    p_credits bigint,
    p_payload_sha256 text,
    p_grant_request_sha256 text)
RETURNS TABLE(status_code text, transaction_id uuid)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $$
DECLARE
    existing_event private.payment_events%ROWTYPE;
    existing_payment private.payment_events%ROWTYPE;
    grant_status text;
    grant_transaction uuid;
    grant_authority text;
    grant_idempotency text;
BEGIN
    IF p_provider IS NULL OR p_event_id IS NULL OR p_payment_id IS NULL OR p_product_id IS NULL
        OR p_amount_minor IS NULL OR p_currency IS NULL OR p_credits IS NULL
        OR p_payload_sha256 IS NULL OR p_grant_request_sha256 IS NULL
        OR p_provider !~ '^[a-z0-9]{1,32}$'
        OR p_event_id !~ '^[A-Za-z0-9_.:-]{1,128}$'
        OR p_payment_id !~ '^[A-Za-z0-9_.:-]{1,120}$'
        OR p_user_id IS NULL OR p_user_id = '00000000-0000-0000-0000-000000000000'::uuid
        OR p_product_id !~ '^[A-Za-z0-9_.-]{1,64}$'
        OR p_amount_minor <= 0 OR p_amount_minor > 1000000000000
        OR p_currency !~ '^[a-z]{3}$'
        OR p_credits <= 0 OR p_credits > 1000000000
        OR p_payload_sha256 !~ '^[0-9A-F]{64}$'
        OR p_grant_request_sha256 !~ '^[0-9A-F]{64}$' THEN
        RAISE EXCEPTION 'verified payment request invalid'
            USING ERRCODE = 'P0001', CONSTRAINT = 'PAYMENT_REQUEST_INVALID';
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended(p_provider || ':' || p_payment_id, 0));
    SELECT e.* INTO existing_event FROM private.payment_events e
        WHERE e.provider = p_provider AND e.provider_event_id = p_event_id;
    SELECT e.* INTO existing_payment FROM private.payment_events e
        WHERE e.provider = p_provider AND e.provider_payment_id = p_payment_id;

    IF existing_event.payment_event_id IS NOT NULL THEN
        IF existing_event.provider_payment_id <> p_payment_id
            OR existing_event.user_id <> p_user_id
            OR existing_event.product_id <> p_product_id
            OR existing_event.amount_minor <> p_amount_minor
            OR existing_event.currency::text <> p_currency
            OR existing_event.credits <> p_credits
            OR existing_event.payload_sha256::text <> p_payload_sha256
            OR existing_event.grant_request_sha256::text <> p_grant_request_sha256 THEN
            RAISE EXCEPTION 'payment event payload conflict'
                USING ERRCODE = 'P0001', CONSTRAINT = 'PAYMENT_EVENT_CONFLICT';
        END IF;
        RETURN QUERY SELECT 'PAYMENT_IDEMPOTENT_REPLAY', existing_event.grant_transaction_id;
        RETURN;
    END IF;

    IF existing_payment.payment_event_id IS NOT NULL THEN
        IF existing_payment.user_id <> p_user_id
            OR existing_payment.product_id <> p_product_id
            OR existing_payment.amount_minor <> p_amount_minor
            OR existing_payment.currency::text <> p_currency
            OR existing_payment.credits <> p_credits
            OR existing_payment.grant_request_sha256::text <> p_grant_request_sha256 THEN
            RAISE EXCEPTION 'payment identity conflict'
                USING ERRCODE = 'P0001', CONSTRAINT = 'PAYMENT_EVENT_CONFLICT';
        END IF;
        RETURN QUERY SELECT 'PAYMENT_IDEMPOTENT_REPLAY', existing_payment.grant_transaction_id;
        RETURN;
    END IF;

    grant_authority := p_provider || ':' || p_payment_id;
    grant_idempotency := p_provider || '.' || encode(sha256(p_payment_id::bytea), 'hex');
    SELECT g.status_code, g.transaction_id INTO grant_status, grant_transaction
        FROM private.credit_grant(p_user_id, p_credits, grant_authority,
            grant_idempotency, p_grant_request_sha256) AS g;
    IF grant_status NOT IN ('CREDIT_APPLIED', 'CREDIT_IDEMPOTENT_REPLAY')
        OR grant_transaction IS NULL THEN
        RAISE EXCEPTION 'credit grant failed'
            USING ERRCODE = 'P0001', CONSTRAINT = 'PAYMENT_GRANT_INVALID';
    END IF;

    INSERT INTO private.payment_events(provider, provider_event_id, provider_payment_id,
        user_id, product_id, amount_minor, currency, credits, payload_sha256,
        grant_request_sha256, grant_transaction_id)
    VALUES (p_provider, p_event_id, p_payment_id, p_user_id, p_product_id,
        p_amount_minor, p_currency, p_credits, p_payload_sha256,
        p_grant_request_sha256, grant_transaction);
    RETURN QUERY SELECT 'PAYMENT_APPLIED', grant_transaction;
END;
$$;

ALTER TABLE private.payment_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.payment_events FORCE ROW LEVEL SECURITY;

REVOKE ALL ON TABLE private.payment_events FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION private.payment_apply_verified(text, text, text, uuid, text,
    bigint, text, bigint, text, text) FROM PUBLIC, anon, authenticated, service_role;
GRANT SELECT ON TABLE private.payment_events TO service_role;
GRANT EXECUTE ON FUNCTION private.payment_apply_verified(text, text, text, uuid, text,
    bigint, text, bigint, text, text) TO service_role;

COMMIT;
