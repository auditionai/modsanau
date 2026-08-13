# Ma trận kiểm thử bảo mật — PLAN 89

## Mục tiêu và cách dùng

Ma trận này ánh xạ đúng 12 yêu cầu của PLAN 89 tới bằng chứng thực thi. Bản máy đọc được nằm tại
[`security-test-matrix.json`](security-test-matrix.json). Gate chuẩn là:

```powershell
powershell -NoProfile -File scripts/Invoke-SecurityTestMatrix.ps1 -Configuration Release
```

Runner fail closed nếu thiếu PostgreSQL cô lập, fixture `acv.exe` đúng hash, output Release của App/Gateway hoặc bất kỳ test/secret
scan nào. PostgreSQL phải là loopback; runner không tự kết nối staging/live. Các test thật không được thay bằng kiểm tra tên file.

## Ma trận

| ID | Yêu cầu | Bằng chứng chính | Bất biến được bảo vệ |
|---|---|---|---|
| STM-01 | Modified `acv.exe` rejected | `ArchiveToolIntegrityPolicyTests.Modified_byte_is_rejected_with_hash_mismatch` | Tool sai SHA-256 không được khởi chạy. |
| STM-02 | Corrupt template rejected | `ProjectArchiveWorkspaceServiceTests.Expected_source_hash_mismatch_returns_structured_failure` | Working copy không được tạo từ template/archive sai hash. |
| STM-03 | Path traversal rejected | `IntegrationTests.PathSecurityTests` | Absolute path, `..`, rooted segment và symlink/reparse escape bị chặn. |
| STM-04 | Forged local credit ignored | Hai negative HTTP case trong `TrustedGatewayEndpointTests` | Client không thể gửi balance/cost/user authority để tạo credit. |
| STM-05 | Expired token rejected | `SupabaseAccessTokenValidatorTests.Expired_overlong_or_cross_subject_token_is_rejected_fail_closed` | Bearer hết hạn/sai subject không được xác thực. |
| STM-06 | Duplicate AI charge idempotent | `SecurityMatrixPostgresTests.Duplicate_ai_enqueue_reserves_credit_exactly_once_and_replays_one_job` | Tám request cùng key chỉ tạo một job, một reservation và một reserve ledger. |
| STM-07 | Concurrent wallet spend safe | `CreditConcurrencyIntegrationTests.Parallel_reservations_cannot_overspend_one_wallet` | Row/advisory lock giữ tổng available/reserved hợp lệ. |
| STM-08 | Wrong user cannot read another project/job | `SecurityMatrixPostgresTests.Wrong_user_cannot_get_list_or_cancel_another_users_ai_job` | Get/list/cancel đều scope theo verified user; cross-user trả not-found/empty. |
| STM-09 | Update with wrong signature/identity rejected | `AppUpdateVerificationTests` và `MsixPackageIdentityVerifierTests` | Manifest sai chữ ký/product/channel/origin/rollout hoặc MSIX sai identity/x64/version/publisher/hash bị từ chối trước typed install. |
| STM-10 | Leaked plaintext token search returns none | `SecretScanTests` và `Invoke-SecretScan.ps1` trên source + Release outputs | Pattern token/key/password không xuất hiện trong source, binary, PDB, log/crash được quét. |
| STM-11 | Tampered template package rejected | `PremiumTemplateDistributionTests.Package_bytes_require_exact_length_hash_and_manifest_signature` | Package phải khớp length, SHA-256 và chữ ký manifest. |
| STM-12 | App survives malformed DDS/archive inputs safely | DDS preflight/native tests và real-ACV malformed archive test | Input lỗi trả failure có cấu trúc, không crash, không sửa input/tool và không thoát workspace. |

## Phạm vi bằng chứng

- PostgreSQL 17.6 local kiểm chứng transaction/idempotency/ownership thật; không phải chứng nhận Supabase staging/live.
- `acv.exe` là fixture private ngoài Git, chỉ được dùng bằng absolute path và exact known hash. Test malformed archive không coi output
  text của tool là trusted và giới hạn diagnostic ở 64 KiB.
- Secret scan là defense-in-depth theo pattern, không chứng minh mọi chuỗi tùy ý đều không nhạy cảm. Quy tắc gốc vẫn là không log
  raw bearer, prompt, ảnh, payment/provider secret hoặc nội dung project.
- PLAN 89 không thêm runtime/in-game validation, game discovery, install/patch/launch hay quyền client mới.

## Kết luận gate

Ma trận chỉ đạt `PASS` khi runner và full Debug/Release suite cùng đạt. Hai case symlink phụ thuộc quyền tạo symlink của Windows
được báo riêng trong run tổng; các boundary traversal khác vẫn phải chạy và PASS. Release thương mại vẫn chịu các blocker signing,
redistribution và production verification đã ghi trong checklist/supply-chain record.

## Overlay crash/recovery PLAN 99

Ma trận 12/12 ở trên tiếp tục là security gate chuẩn. PLAN 99 thêm overlay thực thi tại [CRASH_RECOVERY_EVIDENCE.md](CRASH_RECOVERY_EVIDENCE.md), bao phủ ít nhất mười fault point trên archive pipeline, workspace, cache template mã hóa, immutable template publish, updater và PostgreSQL idempotency. Integration test exact-compare bảng tài liệu với typed evidence source, đồng thời kiểm tra residue inventory fail closed.

Overlay không thay thế bất kỳ STM nào và chỉ PASS khi full Debug/Release, security matrix, release review, test archive thật và PostgreSQL gates đều báo kết quả thật. Skip do thiếu quyền symlink hoặc thiếu private fixture phải được ghi rõ, không được đổi thành PASS.
