BEGIN;

-- Catalog rows are versioned. Reissue only the published legacy APP products
-- so their active payment snapshots have the duration managed in the admin
-- package catalog, without changing completed or pending payment orders.
DO $block$
DECLARE
    invalid_count integer;
BEGIN
    SELECT count(*) INTO invalid_count
    FROM private.commercial_packages
    WHERE is_active
      AND archived_at IS NULL
      AND upper(product_id) ~ '^[0-9]+APP$'
      AND credits NOT BETWEEN 1 AND 3650;

    IF invalid_count > 0 THEN
        RAISE EXCEPTION 'Published APP packages must have a duration from 1 to 3650 days.';
    END IF;
END;
$block$;

UPDATE private.payment_products AS product
SET active = false,
    effective_to = clock_timestamp()
WHERE product.effective_to IS NULL
  AND upper(product.product_id) ~ '^[0-9]+APP$';

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
    'subscription'::private.payment_product_type,
    package.display_name,
    package.amount_minor,
    package.credits::integer,
    NULL,
    true,
    package.sort_order,
    clock_timestamp(),
    versions.next_version
FROM private.commercial_packages AS package
CROSS JOIN LATERAL (
    SELECT COALESCE(max(product.version), 0) + 1 AS next_version
    FROM private.payment_products AS product
    WHERE product.product_id = package.product_id
) AS versions
WHERE package.is_active
  AND package.archived_at IS NULL
  AND upper(package.product_id) ~ '^[0-9]+APP$';

COMMIT;
