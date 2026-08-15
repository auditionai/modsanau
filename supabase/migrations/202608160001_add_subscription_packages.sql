BEGIN;

-- Add duration_days column for subscription packages
ALTER TABLE private.commercial_packages
    ADD COLUMN duration_days integer CHECK (duration_days IS NULL OR duration_days BETWEEN 1 AND 3650);

-- Make credits nullable since subscription packages don't need credits
ALTER TABLE private.commercial_packages
    ALTER COLUMN credits DROP NOT NULL;

-- Add constraint: either credits or duration_days must be set, but not both
ALTER TABLE private.commercial_packages
    ADD CONSTRAINT commercial_packages_type_check
    CHECK (
        (credits IS NOT NULL AND credits > 0 AND duration_days IS NULL) OR
        (duration_days IS NOT NULL AND duration_days > 0 AND credits IS NULL)
    );

COMMIT;
