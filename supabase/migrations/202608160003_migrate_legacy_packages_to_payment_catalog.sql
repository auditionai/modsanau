BEGIN;

-- The original admin "Gói nạp" screen stores both credit bundles and
-- application-duration bundles in commercial_packages. Move the nine
-- published packages shown there into the payment catalog consumed by the
-- SePay Edge Function and public landing page.
DO $block$
DECLARE
    credit_count integer;
    duration_count integer;
BEGIN
    SELECT count(*) INTO credit_count
    FROM private.commercial_packages
    WHERE is_active
      AND archived_at IS NULL
      AND upper(product_id) ~ '^[0-9]+CRE$';

    SELECT count(*) INTO duration_count
    FROM private.commercial_packages
    WHERE is_active
      AND archived_at IS NULL
      AND upper(product_id) ~ '^[0-9]+APP$';

    IF credit_count <> 6 OR duration_count <> 3 THEN
        RAISE EXCEPTION 'Expected six CRE and three APP published legacy packages; found % and %.',
            credit_count, duration_count;
    END IF;
END;
$block$;

-- These were the provisional payment catalog entries used while the legacy
-- catalog was being managed. Keep rows for order history, but stop publishing
-- them so the public catalog has exactly the nine managed packages.
UPDATE private.payment_products
SET active = false,
    effective_to = clock_timestamp()
WHERE effective_to IS NULL
  AND product_id IN ('sub-30d', 'sub-90d', 'credits-100', 'credits-500');

INSERT INTO private.payment_products (
    product_id,
    product_type,
    display_name,
    price_vnd,
    duration_days,
    credit_amount,
    active,
    sort_order,
    effective_from,
    version
)
SELECT
    package.product_id,
    CASE
        WHEN upper(package.product_id) ~ '^[0-9]+APP$' THEN 'subscription'::private.payment_product_type
        ELSE 'credits'::private.payment_product_type
    END,
    package.display_name,
    package.amount_minor,
    CASE WHEN upper(package.product_id) ~ '^[0-9]+APP$' THEN package.credits::integer END,
    CASE WHEN upper(package.product_id) ~ '^[0-9]+CRE$' THEN package.credits END,
    true,
    package.sort_order,
    clock_timestamp(),
    1
FROM private.commercial_packages AS package
WHERE package.is_active
  AND package.archived_at IS NULL
  AND upper(package.product_id) ~ '^[0-9]+(CRE|APP)$';

-- Keep the legacy admin screen and the public payment catalog aligned while
-- those nine products continue to be managed from "Gói nạp".
CREATE OR REPLACE FUNCTION private.sync_legacy_package_to_payment_catalog()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, private
AS $function$
DECLARE
    source_product_id text := NEW.product_id;
    source_is_published boolean := NEW.is_active AND NEW.archived_at IS NULL;
    is_duration boolean;
    next_version integer;
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.product_id <> NEW.product_id
       AND upper(OLD.product_id) ~ '^[0-9]+(CRE|APP)$' THEN
        UPDATE private.payment_products
        SET active = false, effective_to = clock_timestamp()
        WHERE product_id = OLD.product_id AND effective_to IS NULL;
    END IF;

    IF upper(source_product_id) !~ '^[0-9]+(CRE|APP)$' THEN
        RETURN NEW;
    END IF;

    UPDATE private.payment_products
    SET active = false, effective_to = clock_timestamp()
    WHERE product_id = source_product_id AND effective_to IS NULL;

    IF NOT source_is_published THEN
        RETURN NEW;
    END IF;

    is_duration := upper(NEW.product_id) ~ '^[0-9]+APP$';
    SELECT COALESCE(max(version), 0) + 1 INTO next_version
    FROM private.payment_products
    WHERE product_id = NEW.product_id;

    INSERT INTO private.payment_products (
        product_id, product_type, display_name, price_vnd, duration_days,
        credit_amount, active, sort_order, effective_from, version
    ) VALUES (
        NEW.product_id,
        CASE WHEN is_duration THEN 'subscription'::private.payment_product_type
             ELSE 'credits'::private.payment_product_type END,
        NEW.display_name,
        NEW.amount_minor,
        CASE WHEN is_duration THEN NEW.credits::integer END,
        CASE WHEN NOT is_duration THEN NEW.credits END,
        true,
        NEW.sort_order,
        clock_timestamp(),
        next_version
    );

    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS sync_legacy_package_to_payment_catalog_before_write
    ON private.commercial_packages;
CREATE TRIGGER sync_legacy_package_to_payment_catalog_before_write
AFTER INSERT OR UPDATE OF product_id, display_name, amount_minor, credits, is_active, sort_order, archived_at
ON private.commercial_packages
FOR EACH ROW EXECUTE FUNCTION private.sync_legacy_package_to_payment_catalog();

REVOKE ALL ON FUNCTION private.sync_legacy_package_to_payment_catalog() FROM PUBLIC, anon, authenticated;

COMMIT;
