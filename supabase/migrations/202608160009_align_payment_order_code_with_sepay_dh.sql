BEGIN;
DO $block$
DECLARE constraint_name text;
BEGIN
    SELECT conname INTO constraint_name FROM pg_constraint
    WHERE conrelid = 'private.payment_orders'::regclass AND contype = 'c'
      AND pg_get_constraintdef(oid) LIKE '%order_code%' AND pg_get_constraintdef(oid) LIKE '%AMS%' LIMIT 1;
    IF constraint_name IS NOT NULL THEN EXECUTE format('ALTER TABLE private.payment_orders DROP CONSTRAINT %I', constraint_name); END IF;
END;
$block$;
ALTER TABLE private.payment_orders ADD CONSTRAINT payment_orders_order_code_format_check
    CHECK (order_code ~ '^(AMS|DH)[A-Z0-9]{12,20}$');
CREATE OR REPLACE FUNCTION private.issue_sepay_payment_code()
RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, private, extensions AS $function$
DECLARE candidate text;
BEGIN
    IF NEW.order_code !~ '^AMS[A-Z0-9]{12,20}$' THEN RETURN NEW; END IF;
    LOOP
        candidate := 'DH' || upper(substr(encode(gen_random_bytes(10), 'hex'), 1, 16));
        EXIT WHEN NOT EXISTS (SELECT 1 FROM private.payment_orders WHERE order_code = candidate);
    END LOOP;
    NEW.order_code := candidate; NEW.payment_content := candidate; RETURN NEW;
END;
$function$;
DROP TRIGGER IF EXISTS issue_sepay_payment_code_before_insert ON private.payment_orders;
CREATE TRIGGER issue_sepay_payment_code_before_insert BEFORE INSERT ON private.payment_orders
FOR EACH ROW EXECUTE FUNCTION private.issue_sepay_payment_code();
REVOKE ALL ON FUNCTION private.issue_sepay_payment_code() FROM PUBLIC, anon, authenticated;
COMMIT;
