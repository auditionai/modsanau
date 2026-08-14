# Runbook tách biệt, phát hiện và rotation secret

## Phạm vi

Áp dụng cho AI provider credentials, Supabase privileged/service-role keys, database connection credentials, payment webhook secrets, private signing keys, archive master encryption keys và user session tokens. Publishable Supabase client key không phải privileged credential nhưng vẫn phải dùng đúng project/origin.

Không lưu các secret trên trong desktop settings, `.audproj`, prompt preset, source control, CI log/artifact, crash report hoặc installer. Obfuscation, Native AOT, hidden file/folder và đổi extension không phải secret store.

## Authority và nơi lưu

| Secret | Owner | Nơi lưu cho phép | Client |
|---|---|---|---|
| AI provider credential | AI Backend Ops | Managed deployment secret store | Cấm |
| Supabase service-role/DB credential | Data Ops | Managed deployment secret store | Cấm |
| Payment webhook secret | Payments Ops | Managed deployment secret store | Cấm |
| Private signing key | Release Security | HSM/key vault với audit và least privilege | Cấm |
| Archive master encryption key | Template Backend Ops | Managed key vault/HSM | Cấm |
| User refresh/session token | Auth + Desktop Security | Windows Credential Manager qua `ISecureSessionStore` | Chỉ user-scoped material cần thiết |

## Phát hiện trước release

1. Build Release sạch.
2. Chạy `scripts/Invoke-SecretScan.ps1` trên source, build/publish output, managed logs, collected crash artifacts và release staging.
3. Chạy dependency vulnerability audit và kiểm tra Git tracked artifacts.
4. Không paste finding value vào issue/chat/log. Chỉ ghi rule, path, owner và incident ID.
5. False positive chỉ được sửa bằng detector/placeholder có lý do và test; không thêm broad path/file allowlist để làm scan xanh.

## Xử lý khi phát hiện hoặc nghi ngờ lộ secret

1. **Contain:** dừng release/deployment liên quan; thu hồi access tạm thời; hạn chế artifact/log distribution.
2. **Rotate tại authority:** tạo credential mới trong provider/key vault bằng channel độc lập. Không tái sử dụng secret cũ.
3. **Deploy atomically:** hỗ trợ overlap ngắn nếu provider cho phép; cập nhật backend secret reference, health check rồi revoke credential cũ.
4. **Invalidate:** revoke token/key cũ, rotate webhook/signing trust metadata khi cần; user session compromise phải sign-out/revoke server-side.
5. **Eradicate:** xóa secret khỏi source/artifact/log/crash history và cache theo retention policy. Git history rewrite là incident-specific destructive action, cần Security/Repository owner phê duyệt.
6. **Verify:** chạy lại scan source/build/log/crash, authentication/authorization negative tests và deployment smoke test không in secret.
7. **Review:** xác định exposure window, consumers, access logs, affected users/transactions và notification obligations.

## Rotation định kỳ và emergency

- Mỗi owner duy trì inventory, expiry, rotation cadence, backup owner và tested rollback ngoài repo.
- Provider/payment/database credentials phải có least privilege và rotation không yêu cầu desktop update.
- Signing key rotation phải phát hành trust transition được xác thực bởi key cũ hoặc out-of-band root đã bảo vệ; mất key không được giải quyết bằng unsigned fallback.
- Archive encryption key rotation phải versioned; không hard-code key cũ vào client để đọc legacy package.
- Không log request/response body, bearer token, connection string hoặc secret trong health/diagnostic path.

## Evidence cần giữ

Giữ CI run ID, artifact hashes, detector version/commit, rotation timestamp, revoked credential identifier (không phải value), deployment version và verifier identity. Evidence không chứa secret hoặc raw sensitive content.

## Failure policy

Scanner finding, secret-store unavailable, rotation persist failure hoặc không xác định được exposure đều fail closed và chặn release. Local file editor/build/export không phụ thuộc commercial secrets và phải tiếp tục hoạt động khi backend unavailable.
