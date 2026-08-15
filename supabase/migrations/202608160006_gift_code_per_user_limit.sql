BEGIN;

-- Thêm cột redemptions_per_user để giới hạn số lần mỗi user có thể dùng gift code
ALTER TABLE private.gift_codes
    ADD COLUMN IF NOT EXISTS redemptions_per_user integer NOT NULL DEFAULT 1;

-- Thêm comment giải thích
COMMENT ON COLUMN private.gift_codes.maximum_redemptions IS 'Tổng số lần gift code có thể được sử dụng (tổng số lượng phát hành)';
COMMENT ON COLUMN private.gift_codes.redemptions_per_user IS 'Số lần tối đa mỗi device_profile_id có thể redeem gift code này';

COMMIT;
