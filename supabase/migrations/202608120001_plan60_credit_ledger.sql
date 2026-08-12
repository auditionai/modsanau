BEGIN;

CREATE SCHEMA IF NOT EXISTS private;
REVOKE ALL ON SCHEMA private FROM PUBLIC, anon, authenticated;

CREATE TABLE private.credit_wallets (
    user_id uuid PRIMARY KEY,
    available_credits bigint NOT NULL DEFAULT 0 CHECK (available_credits >= 0),
    reserved_credits bigint NOT NULL DEFAULT 0 CHECK (reserved_credits >= 0),
    version bigint NOT NULL DEFAULT 0 CHECK (version >= 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE private.credit_reservations (
    reservation_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL REFERENCES private.credit_wallets(user_id),
    reserved_credits bigint NOT NULL CHECK (reserved_credits > 0),
    captured_credits bigint CHECK (captured_credits > 0 AND captured_credits <= reserved_credits),
    status text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'captured', 'released')),
    reserve_transaction_id uuid UNIQUE,
    terminal_transaction_id uuid UNIQUE,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    closed_at timestamptz,
    CHECK ((status = 'open' AND captured_credits IS NULL AND closed_at IS NULL)
        OR (status = 'captured' AND captured_credits IS NOT NULL AND closed_at IS NOT NULL)
        OR (status = 'released' AND captured_credits IS NULL AND closed_at IS NOT NULL))
);

CREATE INDEX credit_reservations_user_status_idx
    ON private.credit_reservations(user_id, status);

CREATE TABLE private.credit_ledger (
    transaction_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL REFERENCES private.credit_wallets(user_id),
    entry_type text NOT NULL CHECK (entry_type IN ('grant', 'reserve', 'capture', 'release', 'refund')),
    amount bigint NOT NULL CHECK (amount > 0),
    available_delta bigint NOT NULL,
    reserved_delta bigint NOT NULL,
    available_after bigint NOT NULL CHECK (available_after >= 0),
    reserved_after bigint NOT NULL CHECK (reserved_after >= 0),
    reservation_id uuid REFERENCES private.credit_reservations(reservation_id),
    related_transaction_id uuid REFERENCES private.credit_ledger(transaction_id),
    authority_reference varchar(128),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

ALTER TABLE private.credit_reservations
    ADD CONSTRAINT credit_reservations_reserve_transaction_fk
        FOREIGN KEY (reserve_transaction_id) REFERENCES private.credit_ledger(transaction_id),
    ADD CONSTRAINT credit_reservations_terminal_transaction_fk
        FOREIGN KEY (terminal_transaction_id) REFERENCES private.credit_ledger(transaction_id);

CREATE INDEX credit_ledger_user_created_idx
    ON private.credit_ledger(user_id, created_at, transaction_id);
CREATE INDEX credit_ledger_related_transaction_idx
    ON private.credit_ledger(related_transaction_id)
    WHERE related_transaction_id IS NOT NULL;

CREATE TABLE private.credit_refunds (
    refund_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL REFERENCES private.credit_wallets(user_id),
    captured_transaction_id uuid NOT NULL REFERENCES private.credit_ledger(transaction_id),
    refund_transaction_id uuid NOT NULL UNIQUE REFERENCES private.credit_ledger(transaction_id),
    amount bigint NOT NULL CHECK (amount > 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX credit_refunds_capture_idx
    ON private.credit_refunds(user_id, captured_transaction_id);

CREATE TABLE private.credit_idempotency (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL,
    operation text NOT NULL CHECK (operation IN ('grant', 'reserve', 'capture', 'release', 'refund')),
    idempotency_key varchar(128) NOT NULL CHECK (idempotency_key ~ '^[A-Za-z0-9_.:-]{1,128}$'),
    request_hash char(64) NOT NULL CHECK (request_hash ~ '^[0-9A-F]{64}$'),
    available_credits bigint NOT NULL CHECK (available_credits >= 0),
    reserved_credits bigint NOT NULL CHECK (reserved_credits >= 0),
    reservation_id uuid,
    transaction_id uuid,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (user_id, operation, idempotency_key)
);

CREATE OR REPLACE FUNCTION private.reject_credit_immutable_mutation()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, private
AS $$
BEGIN
    RAISE EXCEPTION 'append-only credit record cannot be changed'
        USING ERRCODE = '55000';
END;
$$;

CREATE TRIGGER credit_ledger_append_only
BEFORE UPDATE OR DELETE ON private.credit_ledger
FOR EACH ROW EXECUTE FUNCTION private.reject_credit_immutable_mutation();

CREATE TRIGGER credit_refunds_append_only
BEFORE UPDATE OR DELETE ON private.credit_refunds
FOR EACH ROW EXECUTE FUNCTION private.reject_credit_immutable_mutation();

CREATE TRIGGER credit_idempotency_append_only
BEFORE UPDATE OR DELETE ON private.credit_idempotency
FOR EACH ROW EXECUTE FUNCTION private.reject_credit_immutable_mutation();

CREATE OR REPLACE FUNCTION private.assert_credit_request(
    p_user_id uuid, p_idempotency_key text, p_request_hash text)
RETURNS void
LANGUAGE plpgsql
IMMUTABLE
SET search_path = pg_catalog, private
AS $$
BEGIN
    IF p_user_id IS NULL OR p_user_id = '00000000-0000-0000-0000-000000000000'::uuid
        OR p_idempotency_key !~ '^[A-Za-z0-9_.:-]{1,128}$'
        OR p_request_hash !~ '^[0-9A-F]{64}$' THEN
        RAISE EXCEPTION 'credit request invalid'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_REQUEST_INVALID';
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION private.credit_grant(
    p_user_id uuid,
    p_amount bigint,
    p_authority_reference text,
    p_idempotency_key text,
    p_request_hash text)
RETURNS TABLE(status_code text, available_credits bigint, reserved_credits bigint,
    reservation_id uuid, transaction_id uuid)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $$
DECLARE
    existing private.credit_idempotency%ROWTYPE;
    wallet private.credit_wallets%ROWTYPE;
    new_transaction_id uuid := gen_random_uuid();
BEGIN
    PERFORM private.assert_credit_request(p_user_id, p_idempotency_key, p_request_hash);
    IF p_amount <= 0 OR p_authority_reference !~ '^[A-Za-z0-9_.:-]{1,128}$' THEN
        RAISE EXCEPTION 'invalid credit grant authority'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_AUTHORITY_REFERENCE_INVALID';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended(p_user_id::text || ':grant:' || p_idempotency_key, 0));
    SELECT i.* INTO existing FROM private.credit_idempotency i
        WHERE i.user_id = p_user_id AND i.operation = 'grant' AND i.idempotency_key = p_idempotency_key;
    IF existing.id IS NOT NULL THEN
        IF existing.request_hash::text <> p_request_hash THEN
            RAISE EXCEPTION 'idempotency payload conflict'
                USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_IDEMPOTENCY_CONFLICT';
        END IF;
        RETURN QUERY SELECT 'CREDIT_IDEMPOTENT_REPLAY', existing.available_credits,
            existing.reserved_credits, existing.reservation_id, existing.transaction_id;
        RETURN;
    END IF;

    INSERT INTO private.credit_wallets(user_id) VALUES (p_user_id) ON CONFLICT DO NOTHING;
    SELECT w.* INTO wallet FROM private.credit_wallets w WHERE w.user_id = p_user_id FOR UPDATE;
    IF wallet.available_credits > 9223372036854775807 - p_amount THEN
        RAISE EXCEPTION 'credit overflow'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_OVERFLOW';
    END IF;
    UPDATE private.credit_wallets SET available_credits = available_credits + p_amount,
        version = version + 1, updated_at = clock_timestamp() WHERE user_id = p_user_id
        RETURNING * INTO wallet;
    INSERT INTO private.credit_ledger(transaction_id, user_id, entry_type, amount,
        available_delta, reserved_delta, available_after, reserved_after, authority_reference)
        VALUES (new_transaction_id, p_user_id, 'grant', p_amount, p_amount, 0,
            wallet.available_credits, wallet.reserved_credits, p_authority_reference);
    INSERT INTO private.credit_idempotency(user_id, operation, idempotency_key, request_hash,
        available_credits, reserved_credits, transaction_id)
        VALUES (p_user_id, 'grant', p_idempotency_key, p_request_hash,
            wallet.available_credits, wallet.reserved_credits, new_transaction_id);
    RETURN QUERY SELECT 'CREDIT_APPLIED', wallet.available_credits, wallet.reserved_credits,
        NULL::uuid, new_transaction_id;
END;
$$;

CREATE OR REPLACE FUNCTION private.credit_reserve(
    p_user_id uuid, p_amount bigint, p_idempotency_key text, p_request_hash text)
RETURNS TABLE(status_code text, available_credits bigint, reserved_credits bigint,
    reservation_id uuid, transaction_id uuid)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $$
DECLARE
    existing private.credit_idempotency%ROWTYPE;
    wallet private.credit_wallets%ROWTYPE;
    new_reservation_id uuid := gen_random_uuid();
    new_transaction_id uuid := gen_random_uuid();
BEGIN
    PERFORM private.assert_credit_request(p_user_id, p_idempotency_key, p_request_hash);
    PERFORM pg_advisory_xact_lock(hashtextextended(p_user_id::text || ':reserve:' || p_idempotency_key, 0));
    SELECT i.* INTO existing FROM private.credit_idempotency i
        WHERE i.user_id = p_user_id AND i.operation = 'reserve' AND i.idempotency_key = p_idempotency_key;
    IF existing.id IS NOT NULL THEN
        IF existing.request_hash::text <> p_request_hash THEN
            RAISE EXCEPTION 'idempotency payload conflict'
                USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_IDEMPOTENCY_CONFLICT';
        END IF;
        RETURN QUERY SELECT 'CREDIT_IDEMPOTENT_REPLAY', existing.available_credits,
            existing.reserved_credits, existing.reservation_id, existing.transaction_id;
        RETURN;
    END IF;
    INSERT INTO private.credit_wallets(user_id) VALUES (p_user_id) ON CONFLICT DO NOTHING;
    SELECT w.* INTO wallet FROM private.credit_wallets w WHERE w.user_id = p_user_id FOR UPDATE;
    IF p_amount <= 0 OR wallet.available_credits < p_amount THEN
        RAISE EXCEPTION 'insufficient credit'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_INSUFFICIENT';
    END IF;
    IF wallet.reserved_credits > 9223372036854775807 - p_amount THEN
        RAISE EXCEPTION 'credit overflow'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_OVERFLOW';
    END IF;
    UPDATE private.credit_wallets SET available_credits = available_credits - p_amount,
        reserved_credits = reserved_credits + p_amount, version = version + 1,
        updated_at = clock_timestamp() WHERE user_id = p_user_id RETURNING * INTO wallet;
    INSERT INTO private.credit_reservations(reservation_id, user_id, reserved_credits)
        VALUES (new_reservation_id, p_user_id, p_amount);
    INSERT INTO private.credit_ledger(transaction_id, user_id, entry_type, amount,
        available_delta, reserved_delta, available_after, reserved_after, reservation_id)
        VALUES (new_transaction_id, p_user_id, 'reserve', p_amount, -p_amount, p_amount,
            wallet.available_credits, wallet.reserved_credits, new_reservation_id);
    UPDATE private.credit_reservations SET reserve_transaction_id = new_transaction_id
        WHERE reservation_id = new_reservation_id;
    INSERT INTO private.credit_idempotency(user_id, operation, idempotency_key, request_hash,
        available_credits, reserved_credits, reservation_id, transaction_id)
        VALUES (p_user_id, 'reserve', p_idempotency_key, p_request_hash,
            wallet.available_credits, wallet.reserved_credits, new_reservation_id, new_transaction_id);
    RETURN QUERY SELECT 'CREDIT_APPLIED', wallet.available_credits, wallet.reserved_credits,
        new_reservation_id, new_transaction_id;
END;
$$;

CREATE OR REPLACE FUNCTION private.credit_capture(
    p_user_id uuid, p_reservation_id uuid, p_final_cost bigint,
    p_idempotency_key text, p_request_hash text)
RETURNS TABLE(status_code text, available_credits bigint, reserved_credits bigint,
    reservation_id uuid, transaction_id uuid)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $$
DECLARE
    existing private.credit_idempotency%ROWTYPE;
    wallet private.credit_wallets%ROWTYPE;
    reservation private.credit_reservations%ROWTYPE;
    new_transaction_id uuid := gen_random_uuid();
    unused bigint;
BEGIN
    PERFORM private.assert_credit_request(p_user_id, p_idempotency_key, p_request_hash);
    PERFORM pg_advisory_xact_lock(hashtextextended(p_user_id::text || ':capture:' || p_idempotency_key, 0));
    SELECT i.* INTO existing FROM private.credit_idempotency i
        WHERE i.user_id = p_user_id AND i.operation = 'capture' AND i.idempotency_key = p_idempotency_key;
    IF existing.id IS NOT NULL THEN
        IF existing.request_hash::text <> p_request_hash THEN
            RAISE EXCEPTION 'idempotency payload conflict'
                USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_IDEMPOTENCY_CONFLICT';
        END IF;
        RETURN QUERY SELECT 'CREDIT_IDEMPOTENT_REPLAY', existing.available_credits,
            existing.reserved_credits, existing.reservation_id, existing.transaction_id;
        RETURN;
    END IF;
    SELECT r.* INTO reservation FROM private.credit_reservations r
        WHERE r.reservation_id = p_reservation_id AND r.user_id = p_user_id FOR UPDATE;
    IF reservation.reservation_id IS NULL THEN
        RAISE EXCEPTION 'reservation not found'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_RESERVATION_INVALID';
    END IF;
    IF reservation.status <> 'open' THEN
        RAISE EXCEPTION 'reservation is closed'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_RESERVATION_NOT_OPEN';
    END IF;
    IF p_final_cost <= 0 OR p_final_cost > reservation.reserved_credits THEN
        RAISE EXCEPTION 'capture cost invalid'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_CAPTURE_COST_INVALID';
    END IF;
    SELECT w.* INTO wallet FROM private.credit_wallets w WHERE w.user_id = p_user_id FOR UPDATE;
    unused := reservation.reserved_credits - p_final_cost;
    UPDATE private.credit_wallets SET available_credits = available_credits + unused,
        reserved_credits = reserved_credits - reservation.reserved_credits,
        version = version + 1, updated_at = clock_timestamp() WHERE user_id = p_user_id
        RETURNING * INTO wallet;
    INSERT INTO private.credit_ledger(transaction_id, user_id, entry_type, amount,
        available_delta, reserved_delta, available_after, reserved_after, reservation_id,
        related_transaction_id)
        VALUES (new_transaction_id, p_user_id, 'capture', p_final_cost, unused,
            -reservation.reserved_credits, wallet.available_credits, wallet.reserved_credits,
            p_reservation_id, reservation.reserve_transaction_id);
    UPDATE private.credit_reservations SET status = 'captured', captured_credits = p_final_cost,
        terminal_transaction_id = new_transaction_id, closed_at = clock_timestamp()
        WHERE reservation_id = p_reservation_id;
    INSERT INTO private.credit_idempotency(user_id, operation, idempotency_key, request_hash,
        available_credits, reserved_credits, reservation_id, transaction_id)
        VALUES (p_user_id, 'capture', p_idempotency_key, p_request_hash,
            wallet.available_credits, wallet.reserved_credits, p_reservation_id, new_transaction_id);
    RETURN QUERY SELECT 'CREDIT_APPLIED', wallet.available_credits, wallet.reserved_credits,
        p_reservation_id, new_transaction_id;
END;
$$;

CREATE OR REPLACE FUNCTION private.credit_release(
    p_user_id uuid, p_reservation_id uuid, p_idempotency_key text, p_request_hash text)
RETURNS TABLE(status_code text, available_credits bigint, reserved_credits bigint,
    reservation_id uuid, transaction_id uuid)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $$
DECLARE
    existing private.credit_idempotency%ROWTYPE;
    wallet private.credit_wallets%ROWTYPE;
    reservation private.credit_reservations%ROWTYPE;
    new_transaction_id uuid := gen_random_uuid();
BEGIN
    PERFORM private.assert_credit_request(p_user_id, p_idempotency_key, p_request_hash);
    PERFORM pg_advisory_xact_lock(hashtextextended(p_user_id::text || ':release:' || p_idempotency_key, 0));
    SELECT i.* INTO existing FROM private.credit_idempotency i
        WHERE i.user_id = p_user_id AND i.operation = 'release' AND i.idempotency_key = p_idempotency_key;
    IF existing.id IS NOT NULL THEN
        IF existing.request_hash::text <> p_request_hash THEN
            RAISE EXCEPTION 'idempotency payload conflict'
                USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_IDEMPOTENCY_CONFLICT';
        END IF;
        RETURN QUERY SELECT 'CREDIT_IDEMPOTENT_REPLAY', existing.available_credits,
            existing.reserved_credits, existing.reservation_id, existing.transaction_id;
        RETURN;
    END IF;
    SELECT r.* INTO reservation FROM private.credit_reservations r
        WHERE r.reservation_id = p_reservation_id AND r.user_id = p_user_id FOR UPDATE;
    IF reservation.reservation_id IS NULL THEN
        RAISE EXCEPTION 'reservation not found'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_RESERVATION_INVALID';
    END IF;
    IF reservation.status <> 'open' THEN
        RAISE EXCEPTION 'reservation is closed'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_RESERVATION_NOT_OPEN';
    END IF;
    SELECT w.* INTO wallet FROM private.credit_wallets w WHERE w.user_id = p_user_id FOR UPDATE;
    UPDATE private.credit_wallets SET available_credits = available_credits + reservation.reserved_credits,
        reserved_credits = reserved_credits - reservation.reserved_credits,
        version = version + 1, updated_at = clock_timestamp() WHERE user_id = p_user_id
        RETURNING * INTO wallet;
    INSERT INTO private.credit_ledger(transaction_id, user_id, entry_type, amount,
        available_delta, reserved_delta, available_after, reserved_after, reservation_id,
        related_transaction_id)
        VALUES (new_transaction_id, p_user_id, 'release', reservation.reserved_credits,
            reservation.reserved_credits, -reservation.reserved_credits,
            wallet.available_credits, wallet.reserved_credits, p_reservation_id,
            reservation.reserve_transaction_id);
    UPDATE private.credit_reservations SET status = 'released', terminal_transaction_id = new_transaction_id,
        closed_at = clock_timestamp() WHERE reservation_id = p_reservation_id;
    INSERT INTO private.credit_idempotency(user_id, operation, idempotency_key, request_hash,
        available_credits, reserved_credits, reservation_id, transaction_id)
        VALUES (p_user_id, 'release', p_idempotency_key, p_request_hash,
            wallet.available_credits, wallet.reserved_credits, p_reservation_id, new_transaction_id);
    RETURN QUERY SELECT 'CREDIT_APPLIED', wallet.available_credits, wallet.reserved_credits,
        p_reservation_id, new_transaction_id;
END;
$$;

CREATE OR REPLACE FUNCTION private.credit_refund(
    p_user_id uuid, p_captured_transaction_id uuid, p_amount bigint,
    p_idempotency_key text, p_request_hash text)
RETURNS TABLE(status_code text, available_credits bigint, reserved_credits bigint,
    reservation_id uuid, transaction_id uuid)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $$
DECLARE
    existing private.credit_idempotency%ROWTYPE;
    wallet private.credit_wallets%ROWTYPE;
    captured private.credit_ledger%ROWTYPE;
    refunded bigint;
    new_transaction_id uuid := gen_random_uuid();
    new_refund_id uuid := gen_random_uuid();
BEGIN
    PERFORM private.assert_credit_request(p_user_id, p_idempotency_key, p_request_hash);
    PERFORM pg_advisory_xact_lock(hashtextextended(p_user_id::text || ':refund:' || p_idempotency_key, 0));
    SELECT i.* INTO existing FROM private.credit_idempotency i
        WHERE i.user_id = p_user_id AND i.operation = 'refund' AND i.idempotency_key = p_idempotency_key;
    IF existing.id IS NOT NULL THEN
        IF existing.request_hash::text <> p_request_hash THEN
            RAISE EXCEPTION 'idempotency payload conflict'
                USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_IDEMPOTENCY_CONFLICT';
        END IF;
        RETURN QUERY SELECT 'CREDIT_IDEMPOTENT_REPLAY', existing.available_credits,
            existing.reserved_credits, existing.reservation_id, existing.transaction_id;
        RETURN;
    END IF;
    SELECT l.* INTO captured FROM private.credit_ledger l
        WHERE l.transaction_id = p_captured_transaction_id AND l.user_id = p_user_id
            AND l.entry_type = 'capture' FOR UPDATE;
    IF captured.transaction_id IS NULL OR p_amount <= 0 THEN
        RAISE EXCEPTION 'captured transaction invalid'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_TRANSACTION_INVALID';
    END IF;
    SELECT COALESCE(sum(r.amount), 0) INTO refunded FROM private.credit_refunds r
        WHERE r.user_id = p_user_id AND r.captured_transaction_id = p_captured_transaction_id;
    IF refunded > captured.amount - p_amount THEN
        RAISE EXCEPTION 'refund exceeds captured cost'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_REFUND_EXCEEDS_CAPTURE';
    END IF;
    SELECT w.* INTO wallet FROM private.credit_wallets w WHERE w.user_id = p_user_id FOR UPDATE;
    IF wallet.available_credits > 9223372036854775807 - p_amount THEN
        RAISE EXCEPTION 'credit overflow'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_OVERFLOW';
    END IF;
    UPDATE private.credit_wallets SET available_credits = available_credits + p_amount,
        version = version + 1, updated_at = clock_timestamp() WHERE user_id = p_user_id
        RETURNING * INTO wallet;
    INSERT INTO private.credit_ledger(transaction_id, user_id, entry_type, amount,
        available_delta, reserved_delta, available_after, reserved_after, reservation_id,
        related_transaction_id)
        VALUES (new_transaction_id, p_user_id, 'refund', p_amount, p_amount, 0,
            wallet.available_credits, wallet.reserved_credits, captured.reservation_id,
            p_captured_transaction_id);
    INSERT INTO private.credit_refunds(refund_id, user_id, captured_transaction_id,
        refund_transaction_id, amount)
        VALUES (new_refund_id, p_user_id, p_captured_transaction_id, new_transaction_id, p_amount);
    INSERT INTO private.credit_idempotency(user_id, operation, idempotency_key, request_hash,
        available_credits, reserved_credits, reservation_id, transaction_id)
        VALUES (p_user_id, 'refund', p_idempotency_key, p_request_hash,
            wallet.available_credits, wallet.reserved_credits, captured.reservation_id, new_transaction_id);
    RETURN QUERY SELECT 'CREDIT_APPLIED', wallet.available_credits, wallet.reserved_credits,
        captured.reservation_id, new_transaction_id;
END;
$$;

ALTER TABLE private.credit_wallets ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.credit_reservations ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.credit_ledger ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.credit_refunds ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.credit_idempotency ENABLE ROW LEVEL SECURITY;

REVOKE ALL ON private.credit_wallets, private.credit_reservations, private.credit_ledger,
    private.credit_refunds, private.credit_idempotency
    FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION private.reject_credit_immutable_mutation(),
    private.assert_credit_request(uuid, text, text),
    private.credit_grant(uuid, bigint, text, text, text),
    private.credit_reserve(uuid, bigint, text, text),
    private.credit_capture(uuid, uuid, bigint, text, text),
    private.credit_release(uuid, uuid, text, text),
    private.credit_refund(uuid, uuid, bigint, text, text)
    FROM PUBLIC, anon, authenticated, service_role;
GRANT USAGE ON SCHEMA private TO service_role;
GRANT SELECT ON private.credit_wallets TO service_role;
GRANT EXECUTE ON FUNCTION private.credit_grant(uuid, bigint, text, text, text) TO service_role;
GRANT EXECUTE ON FUNCTION private.credit_reserve(uuid, bigint, text, text) TO service_role;
GRANT EXECUTE ON FUNCTION private.credit_capture(uuid, uuid, bigint, text, text) TO service_role;
GRANT EXECUTE ON FUNCTION private.credit_release(uuid, uuid, text, text) TO service_role;
GRANT EXECUTE ON FUNCTION private.credit_refund(uuid, uuid, bigint, text, text) TO service_role;

COMMIT;
