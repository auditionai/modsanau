# PLAN 108 — Required Secrets Configuration

## Current Status

✅ **Already Set:**
- `TRAM_SANG_TAO_API_KEY` — Trạm Sáng Tạo API key

🔴 **Missing Secrets:**

### 1. SePay Configuration
```bash
supabase secrets set SEPAY_WEBHOOK_SECRET="<your-sepay-webhook-secret>"
supabase secrets set PAYMENT_BANK_ACCOUNT="<your-bank-account-number>"
supabase secrets set PAYMENT_BANK_ACCOUNT_NAME="<YOUR ACCOUNT NAME>"
supabase secrets set PAYMENT_BANK_CODE="MB"  # hoặc bank code khác
```

### 2. Database Vault — ECDSA Signing Key

Cần generate ECDSA key pair và lưu vào Vault:

**Generate key:**
```bash
# Generate private key
openssl ecparam -name prime256v1 -genkey -noout -out private.pem

# Extract public key
openssl ec -in private.pem -pubout -out public.pem

# Convert private key to base64
cat private.pem | base64 -w 0 > private_base64.txt
```

**Store in Vault:**
```sql
SELECT vault.create_secret(
  'ECDSA_SIGNING_PRIVATE_KEY',
  '<paste base64 content from private_base64.txt>',
  'Device entitlement grant signing key'
);
```

**Update Desktop App:**

File: `src/AuditionModStudio.App/Bootstrap/ProductionSupabaseConfiguration.cs`

Add constant:
```csharp
internal const string EntitlementPublicKeyPem = @"-----BEGIN PUBLIC KEY-----
<paste content from public.pem>
-----END PUBLIC KEY-----";
```

---

## Verification Commands

### Test SePay Webhook After Secrets Set:
```bash
curl -X POST "https://plvuutsjwsawkkrmvigz.supabase.co/functions/v1/sepay-webhook" \
  -H "Content-Type: application/json" \
  -H "x-sepay-timestamp: $(date +%s)" \
  -H "x-sepay-signature: <generate-hmac>" \
  -d '{"id":"test","gateway":"MB","transaction_date":"2026-08-15 10:00:00"}'
```

### Test Payment API After Vault Secret:
```sql
-- Test device identity grant
SELECT * FROM public.device_identity_api('grant', '{"publicKeyPem":"...","hwid":"test"}'::jsonb);
```

---

## Notes

- SePay webhook secret phải match với secret configured trong SePay Dashboard
- ECDSA key pair phải dùng prime256v1 curve (P-256)
- Private key KHÔNG được commit vào git
- Public key CÓ THỂ commit (dùng để verify signatures)
