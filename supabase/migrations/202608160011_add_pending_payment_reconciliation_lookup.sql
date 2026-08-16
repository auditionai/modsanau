BEGIN;
CREATE OR REPLACE FUNCTION public.payment_pending_order_api(input_user_id uuid)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, private AS $function$
DECLARE order_id uuid;
BEGIN
    IF NOT (current_setting('request.jwt.claim.role', true) = 'service_role' OR pg_has_role(current_user, 'service_role', 'MEMBER') OR current_user = 'postgres') THEN RAISE EXCEPTION 'PAYMENT_SERVICE_ROLE_REQUIRED' USING ERRCODE = '42501'; END IF;
    SELECT payment_order_id INTO order_id FROM private.payment_orders
    WHERE auth_user_id = input_user_id AND status = 'waiting_payment' ORDER BY created_at DESC LIMIT 1;
    RETURN CASE WHEN order_id IS NULL THEN NULL ELSE jsonb_build_object('orderId', order_id) END;
END
$function$;
REVOKE ALL ON FUNCTION public.payment_pending_order_api(uuid) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.payment_pending_order_api(uuid) TO service_role;
COMMIT;
