BEGIN;

-- PostgreSQL exposes RETURNS TABLE names as PL/pgSQL variables. Qualify every mutable
-- projection reference so output names can never collide with wallet/reservation columns.
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
    SELECT i.* INTO existing FROM private.credit_idempotency AS i
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
    SELECT w.* INTO wallet FROM private.credit_wallets AS w WHERE w.user_id = p_user_id FOR UPDATE;
    IF wallet.available_credits > 9223372036854775807 - p_amount THEN
        RAISE EXCEPTION 'credit overflow'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_OVERFLOW';
    END IF;
    UPDATE private.credit_wallets AS w SET available_credits = w.available_credits + p_amount,
        version = w.version + 1, updated_at = clock_timestamp() WHERE w.user_id = p_user_id
        RETURNING w.* INTO wallet;
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
    SELECT i.* INTO existing FROM private.credit_idempotency AS i
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
    SELECT w.* INTO wallet FROM private.credit_wallets AS w WHERE w.user_id = p_user_id FOR UPDATE;
    IF p_amount <= 0 OR wallet.available_credits < p_amount THEN
        RAISE EXCEPTION 'insufficient credit'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_INSUFFICIENT';
    END IF;
    IF wallet.reserved_credits > 9223372036854775807 - p_amount THEN
        RAISE EXCEPTION 'credit overflow'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_OVERFLOW';
    END IF;
    UPDATE private.credit_wallets AS w SET available_credits = w.available_credits - p_amount,
        reserved_credits = w.reserved_credits + p_amount, version = w.version + 1,
        updated_at = clock_timestamp() WHERE w.user_id = p_user_id RETURNING w.* INTO wallet;
    INSERT INTO private.credit_reservations(reservation_id, user_id, reserved_credits)
        VALUES (new_reservation_id, p_user_id, p_amount);
    INSERT INTO private.credit_ledger(transaction_id, user_id, entry_type, amount,
        available_delta, reserved_delta, available_after, reserved_after, reservation_id)
        VALUES (new_transaction_id, p_user_id, 'reserve', p_amount, -p_amount, p_amount,
            wallet.available_credits, wallet.reserved_credits, new_reservation_id);
    UPDATE private.credit_reservations AS r SET reserve_transaction_id = new_transaction_id
        WHERE r.reservation_id = new_reservation_id;
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
    SELECT i.* INTO existing FROM private.credit_idempotency AS i
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
    SELECT r.* INTO reservation FROM private.credit_reservations AS r
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
    SELECT w.* INTO wallet FROM private.credit_wallets AS w WHERE w.user_id = p_user_id FOR UPDATE;
    unused := reservation.reserved_credits - p_final_cost;
    UPDATE private.credit_wallets AS w SET available_credits = w.available_credits + unused,
        reserved_credits = w.reserved_credits - reservation.reserved_credits,
        version = w.version + 1, updated_at = clock_timestamp() WHERE w.user_id = p_user_id
        RETURNING w.* INTO wallet;
    INSERT INTO private.credit_ledger(transaction_id, user_id, entry_type, amount,
        available_delta, reserved_delta, available_after, reserved_after, reservation_id,
        related_transaction_id)
        VALUES (new_transaction_id, p_user_id, 'capture', p_final_cost, unused,
            -reservation.reserved_credits, wallet.available_credits, wallet.reserved_credits,
            p_reservation_id, reservation.reserve_transaction_id);
    UPDATE private.credit_reservations AS r SET status = 'captured', captured_credits = p_final_cost,
        terminal_transaction_id = new_transaction_id, closed_at = clock_timestamp()
        WHERE r.reservation_id = p_reservation_id;
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
    SELECT i.* INTO existing FROM private.credit_idempotency AS i
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
    SELECT r.* INTO reservation FROM private.credit_reservations AS r
        WHERE r.reservation_id = p_reservation_id AND r.user_id = p_user_id FOR UPDATE;
    IF reservation.reservation_id IS NULL THEN
        RAISE EXCEPTION 'reservation not found'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_RESERVATION_INVALID';
    END IF;
    IF reservation.status <> 'open' THEN
        RAISE EXCEPTION 'reservation is closed'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_RESERVATION_NOT_OPEN';
    END IF;
    SELECT w.* INTO wallet FROM private.credit_wallets AS w WHERE w.user_id = p_user_id FOR UPDATE;
    UPDATE private.credit_wallets AS w SET available_credits = w.available_credits + reservation.reserved_credits,
        reserved_credits = w.reserved_credits - reservation.reserved_credits,
        version = w.version + 1, updated_at = clock_timestamp() WHERE w.user_id = p_user_id
        RETURNING w.* INTO wallet;
    INSERT INTO private.credit_ledger(transaction_id, user_id, entry_type, amount,
        available_delta, reserved_delta, available_after, reserved_after, reservation_id,
        related_transaction_id)
        VALUES (new_transaction_id, p_user_id, 'release', reservation.reserved_credits,
            reservation.reserved_credits, -reservation.reserved_credits,
            wallet.available_credits, wallet.reserved_credits, p_reservation_id,
            reservation.reserve_transaction_id);
    UPDATE private.credit_reservations AS r SET status = 'released', terminal_transaction_id = new_transaction_id,
        closed_at = clock_timestamp() WHERE r.reservation_id = p_reservation_id;
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
    SELECT i.* INTO existing FROM private.credit_idempotency AS i
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
    SELECT l.* INTO captured FROM private.credit_ledger AS l
        WHERE l.transaction_id = p_captured_transaction_id AND l.user_id = p_user_id
            AND l.entry_type = 'capture' FOR UPDATE;
    IF captured.transaction_id IS NULL OR p_amount <= 0 THEN
        RAISE EXCEPTION 'captured transaction invalid'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_TRANSACTION_INVALID';
    END IF;
    SELECT COALESCE(sum(r.amount), 0) INTO refunded FROM private.credit_refunds AS r
        WHERE r.user_id = p_user_id AND r.captured_transaction_id = p_captured_transaction_id;
    IF refunded > captured.amount - p_amount THEN
        RAISE EXCEPTION 'refund exceeds captured cost'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_REFUND_EXCEEDS_CAPTURE';
    END IF;
    SELECT w.* INTO wallet FROM private.credit_wallets AS w WHERE w.user_id = p_user_id FOR UPDATE;
    IF wallet.available_credits > 9223372036854775807 - p_amount THEN
        RAISE EXCEPTION 'credit overflow'
            USING ERRCODE = 'P0001', CONSTRAINT = 'CREDIT_OVERFLOW';
    END IF;
    UPDATE private.credit_wallets AS w SET available_credits = w.available_credits + p_amount,
        version = w.version + 1, updated_at = clock_timestamp() WHERE w.user_id = p_user_id
        RETURNING w.* INTO wallet;
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

REVOKE ALL ON FUNCTION private.credit_grant(uuid, bigint, text, text, text),
    private.credit_reserve(uuid, bigint, text, text),
    private.credit_capture(uuid, uuid, bigint, text, text),
    private.credit_release(uuid, uuid, text, text),
    private.credit_refund(uuid, uuid, bigint, text, text)
    FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION private.credit_grant(uuid, bigint, text, text, text) TO service_role;
GRANT EXECUTE ON FUNCTION private.credit_reserve(uuid, bigint, text, text) TO service_role;
GRANT EXECUTE ON FUNCTION private.credit_capture(uuid, uuid, bigint, text, text) TO service_role;
GRANT EXECUTE ON FUNCTION private.credit_release(uuid, uuid, text, text) TO service_role;
GRANT EXECUTE ON FUNCTION private.credit_refund(uuid, uuid, bigint, text, text) TO service_role;

COMMIT;
