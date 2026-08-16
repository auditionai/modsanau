BEGIN;
CREATE OR REPLACE FUNCTION public.payment_order_snapshot_api(input_order_id uuid, input_user_id uuid DEFAULT NULL, input_device_code text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, private AS $function$
DECLARE order_row private.payment_orders%ROWTYPE;
BEGIN
    IF NOT (current_setting('request.jwt.claim.role', true) = 'service_role' OR pg_has_role(current_user, 'service_role', 'MEMBER') OR current_user = 'postgres') THEN RAISE EXCEPTION 'PAYMENT_SERVICE_ROLE_REQUIRED' USING ERRCODE = '42501'; END IF;
    SELECT order_item.* INTO order_row FROM private.payment_orders AS order_item JOIN private.device_profiles AS profile ON profile.device_profile_id = order_item.device_profile_id
    WHERE order_item.payment_order_id = input_order_id AND (profile.auth_user_id = input_user_id OR (input_user_id IS NULL AND profile.public_device_code = upper(input_device_code)));
    IF NOT FOUND THEN RAISE EXCEPTION 'PAYMENT_ORDER_NOT_FOUND' USING ERRCODE = 'P0001'; END IF;
    RETURN jsonb_build_object('orderId', order_row.payment_order_id, 'orderCode', order_row.order_code, 'status', order_row.status,
        'productName', order_row.product_name, 'productType', order_row.product_type, 'priceVnd', order_row.price_vnd,
        'durationDays', order_row.duration_days, 'creditAmount', order_row.credit_amount, 'currency', order_row.currency,
        'paymentContent', order_row.payment_content, 'createdAt', order_row.created_at, 'expiresAt', order_row.expires_at,
        'paidAt', order_row.paid_at, 'fulfilledAt', order_row.fulfilled_at, 'reviewReason', order_row.review_reason);
END
$function$;
REVOKE ALL ON FUNCTION public.payment_order_snapshot_api(uuid,uuid,text) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.payment_order_snapshot_api(uuid,uuid,text) TO service_role;
COMMIT;
