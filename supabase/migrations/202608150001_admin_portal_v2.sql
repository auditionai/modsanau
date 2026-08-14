BEGIN;

CREATE TYPE private.managed_user_status AS ENUM ('active', 'suspended', 'deactivated');

CREATE TABLE private.managed_user_profiles (
    user_id uuid PRIMARY KEY REFERENCES auth.users(id) ON DELETE RESTRICT,
    display_name varchar(128),
    contact_email varchar(320),
    status private.managed_user_status NOT NULL DEFAULT 'active',
    internal_note varchar(1000),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK (display_name IS NULL OR (length(display_name) BETWEEN 1 AND 128 AND NOT display_name ~ '[[:cntrl:]]')),
    CHECK (contact_email IS NULL OR (length(contact_email) BETWEEN 3 AND 320 AND contact_email LIKE '%@%'
        AND NOT contact_email ~ '[[:cntrl:]]')),
    CHECK (internal_note IS NULL OR NOT internal_note ~ '[[:cntrl:]]')
);

CREATE TABLE private.commercial_packages (
    package_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id varchar(64) NOT NULL UNIQUE CHECK (product_id ~ '^[A-Za-z0-9_.-]{1,64}$'),
    display_name varchar(128) NOT NULL CHECK (length(display_name) BETWEEN 1 AND 128
        AND NOT display_name ~ '[[:cntrl:]]'),
    description varchar(500),
    amount_minor bigint NOT NULL CHECK (amount_minor BETWEEN 1 AND 1000000000000),
    currency char(3) NOT NULL CHECK (currency ~ '^[a-z]{3}$'),
    credits bigint NOT NULL CHECK (credits BETWEEN 1 AND 1000000000),
    is_active boolean NOT NULL DEFAULT true,
    sort_order integer NOT NULL DEFAULT 0 CHECK (sort_order BETWEEN -100000 AND 100000),
    archived_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK (description IS NULL OR NOT description ~ '[[:cntrl:]]'),
    CHECK ((is_active AND archived_at IS NULL) OR NOT is_active)
);

CREATE INDEX managed_user_profiles_status_idx
    ON private.managed_user_profiles(status, updated_at DESC);
CREATE INDEX commercial_packages_active_sort_idx
    ON private.commercial_packages(is_active, sort_order, created_at);
CREATE INDEX payment_events_verified_at_idx
    ON private.payment_events(verified_at DESC, payment_event_id DESC);
CREATE INDEX payment_events_user_time_idx
    ON private.payment_events(user_id, verified_at DESC);

ALTER TABLE private.managed_user_profiles ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.managed_user_profiles FORCE ROW LEVEL SECURITY;
ALTER TABLE private.commercial_packages ENABLE ROW LEVEL SECURITY;
ALTER TABLE private.commercial_packages FORCE ROW LEVEL SECURITY;

REVOKE ALL ON private.managed_user_profiles, private.commercial_packages
    FROM PUBLIC, anon, authenticated;
GRANT SELECT, INSERT, UPDATE ON private.managed_user_profiles, private.commercial_packages TO service_role;

COMMIT;
