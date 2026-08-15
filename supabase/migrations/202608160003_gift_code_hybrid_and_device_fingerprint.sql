-- Thêm loại 'hybrid' vào enum (phải chạy NGOÀI transaction)
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'hybrid' AND enumtypid = 'private.gift_code_kind'::regtype) THEN
        ALTER TYPE private.gift_code_kind ADD VALUE 'hybrid';
    END IF;
END $$;

-- Các thay đổi còn lại trong transaction
BEGIN;

-- 1. Cho phép gift code vừa có credits VÀ duration_days (hoặc chỉ một trong hai = 0)
ALTER TABLE private.gift_codes DROP CONSTRAINT IF EXISTS gift_codes_check;
ALTER TABLE private.gift_codes
    ADD CONSTRAINT gift_codes_hybrid_check CHECK (
        (kind = 'duration' AND duration_days IS NOT NULL AND duration_days > 0) OR
        (kind = 'credits' AND credit_amount IS NOT NULL AND credit_amount > 0) OR
        (kind = 'hybrid' AND
            (COALESCE(duration_days, 0) > 0 OR COALESCE(credit_amount, 0) > 0))
    );

-- 2. Thêm cột device_fingerprint vào device_profiles để track thiết bị vật lý
ALTER TABLE private.device_profiles
    ADD COLUMN IF NOT EXISTS device_fingerprint varchar(128);

-- 3. Thêm index cho device_fingerprint
CREATE INDEX IF NOT EXISTS device_profiles_fingerprint_idx
    ON private.device_profiles(device_fingerprint) WHERE device_fingerprint IS NOT NULL;

-- 4. Thêm constraint: gift code chỉ được redeem 1 lần per device_fingerprint
-- (ngăn chặn spam tạo tài khoản mới hoặc xóa app trên cùng một thiết bị vật lý)
ALTER TABLE private.gift_code_redemptions
    ADD COLUMN IF NOT EXISTS device_fingerprint varchar(128);

CREATE UNIQUE INDEX IF NOT EXISTS gift_code_redemptions_fingerprint_unique
    ON private.gift_code_redemptions(gift_code_id, device_fingerprint)
    WHERE device_fingerprint IS NOT NULL;

COMMIT;
