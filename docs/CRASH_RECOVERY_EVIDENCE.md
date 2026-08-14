# Bằng chứng crash/recovery — PLAN 99

Tài liệu này là bản đọc được của ma trận test-only trong `CrashRecoveryEvidence`. Integration test so sánh nguyên văn phần bảng để tài liệu không thể drift khỏi nguồn sự thật thực thi. Ma trận chỉ mô phỏng crash, hủy, lỗi I/O, corruption và replay tại các boundary đã có; không thêm cơ chế recovery tự động vào production và không mở rộng file-only boundary.

## Mô hình trạng thái

- `ĐÃ COMMIT`: artifact đã qua điểm commit bất biến; retry trùng phải idempotent hoặc bị từ chối an toàn.
- `WORKSPACE CÓ THỂ RECOVER`: workspace có marker hợp lệ, lock không còn active và chỉ được reopen sau thao tác tường minh.
- `GIAO DỊCH DỞ DANG`: staging, `.tmp`, `.partial` hoặc candidate chưa promote; không được xem là output hợp lệ.
- `RESIDUE CŨ`: residue hợp lệ về hình dạng nhưng hết vòng đời; chỉ cleanup theo policy và exact path.
- `ARTIFACT HỎNG`: hash/marker cho biết dữ liệu không còn toàn vẹn; fail closed và không materialize/promote.
- `LẠ / KHÔNG TIN CẬY`: trạng thái không nhận diện hoặc dựa trên reparse; không tự trust, follow hay promote.

## Ma trận thực thi

<!-- MATRIX:START -->
| Case ID | Thao tác | Điểm crash | Mô phỏng | Trạng thái sau lỗi | Quy tắc recovery / retry | Bằng chứng thực thi |
|---|---|---|---|---|---|---|
| `archive-apply-before-promote` | Áp dụng DDS | candidate đã validate, lưu project lỗi trước commit | LỖI I/O | GIAO DỊCH DỞ DANG | khôi phục đúng byte DDS đang làm việc, xóa history mới và cho phép retry sạch | `Projects.Tests.TextureApplyServiceTests.Project_save_failure_rolls_back_target_and_new_history_assets` |
| `archive-build-pack-before-promote` | Build archive | pack hoặc lần lưu cuối lỗi quanh bước promote output | LỖI I/O | GIAO DỊCH DỞ DANG | không công bố output dở dang và khôi phục đúng byte output cũ nếu đã promote | `Projects.Tests.ProjectBuildServiceTests.Exception_after_promotion_still_restores_previous_output_bytes` |
| `archive-export-after-durable-copy` | Export archive standalone | copy bền vững vào file tạm xong trước promote đích | LỖI I/O | GIAO DỊCH DỞ DANG | giữ nguyên byte đích, xóa residue giao dịch và copy lại khi retry | `Projects.Tests.ArchiveExportServiceTests.One_shot_failure_after_durable_copy_preserves_destination_and_retry_succeeds` |
| `archive-extract-staging` | Extract archive | tool thoát sau khi ghi một DDS vào staging | THOÁT TIẾN TRÌNH | GIAO DỊCH DỞ DANG | cây staging không phải trạng thái project đã commit và retry sạch phải thành công | `Archives.Tests.AcvTool5RunnerTests.Extract_process_failure_after_staging_write_is_incomplete_and_clean_retry_succeeds` |
| `archive-scan-preparation` | Quét Smart Mod | hủy khi các tác vụ metadata đang chạy | HỦY | GIAO DỊCH DỞ DANG | không công bố kết quả quét một phần và retry tính lại inventory xác định | `Projects.Tests.SmartModScanServiceTests.Cancellation_is_structured_and_does_not_publish_partial_result` |
| `cache-encrypted-mid-chunk` | Cache template mã hóa | sau khi chunk mã hóa đầu tiên vào entry tạm | HỦY | GIAO DỊCH DỞ DANG | giữ đúng ciphertext đã commit, xóa temp và retry từ nguồn | `Security.Tests.EncryptedPremiumTemplateCacheTests.Crash_after_first_encrypted_chunk_preserves_old_entry_and_retry_recovers` |
| `payment-duplicate-delivery` | Fulfillment thanh toán | phân phối đồng thời cùng một provider event đã xác minh | REPLAY ĐỒNG THỜI | ĐÃ COMMIT | server ledger chỉ áp dụng một lần và mọi bản trùng trả idempotent replay | `Gateway.Tests.CreditConcurrencyIntegrationTests.Concurrent_verified_payment_delivery_applies_once_and_replays_without_double_credit` |
| `template-after-immutable-commit` | Publish template admin | fault ngay sau khi move thư mục immutable | CHECKPOINT XÁC ĐỊNH | ĐÃ COMMIT | giữ phiên bản immutable đầy đủ và từ chối retry trùng | `Archives.Tests.AtomicFileTemplateAdminPublisherTests.Crash_after_immutable_commit_keeps_complete_version_and_retry_cannot_duplicate_it` |
| `template-before-immutable-commit` | Publish template admin | package mã hóa hoặc metadata đã ghi trước move immutable | CHECKPOINT XÁC ĐỊNH | GIAO DỊCH DỞ DANG | xóa staging, giữ phiên bản trước và cho phép retry sạch | `Archives.Tests.AtomicFileTemplateAdminPublisherTests.Crash_before_immutable_commit_preserves_published_version_and_clean_retry_succeeds` |
| `updater-before-installer-handoff` | Cập nhật ứng dụng | candidate đã xác minh bị installer handoff từ chối | LỖI I/O | GIAO DỊCH DỞ DANG | xóa operation root và retry phải download, hash, verify lại | `Security.Tests.AppUpdateVerificationTests.Failed_verified_handoff_is_cleaned_and_retry_redownloads_and_reverifies` |
| `updater-partial-download` | Cập nhật ứng dụng | download bị hủy trước promote candidate | HỦY | GIAO DỊCH DỞ DANG | xóa partial và operation root, tuyệt đối không gọi installer | `Security.Tests.AppUpdateVerificationTests.Cancellation_cleans_staging_and_never_installs` |
| `workspace-abandoned-retained` | Secure workspace | process biến mất sau retention marker và nhả lock | THOÁT TIẾN TRÌNH | WORKSPACE CÓ THỂ RECOVER | phát hiện không mutation và chỉ recover đúng candidate sau thao tác tường minh | `IntegrationTests.SecureWorkspaceTests.Startup_detects_stale_retained_workspace_without_automatic_mutation` |
| `workspace-unknown-reparse` | Inventory recovery | residue lạ hoặc dựa trên reparse xuất hiện dưới managed root | HỎNG DỮ LIỆU | LẠ / KHÔNG TIN CẬY | không tự trust, tự promote hoặc đi theo residue | `IntegrationTests.Plan99CrashRecoveryMatrixTests.Residue_inventory_is_deterministic_and_unknown_state_never_becomes_trusted` |
<!-- MATRIX:END -->

## Fixture thật và bất biến file-only

Gate archive thật tiếp tục dùng working copy của private fixture `015.ab`, `015.keydat`, `acv.exe` và cây extract 320 file. Các hash pristine được kiểm tra cả trước và sau pipeline:

- `015.ab`: `3C4272BDDDA815B9F9E832D5FCF9EE10E2417301726C895CDB312DFF675C6081`.
- `015.keydat`: `78F2910819D155CFBF0E366A80FEA4F40126BEA8C201E25EBDB0E0F956E2297F`.
- `acv.exe`: `6A52C808D7E5A59EB41E43D86A32E78067F424C8531E34981887F093E81547D3`.
- DDS đích pristine `texture/hud/pointer.dds`: `854189B91972C17C483C2434FC077B1F94AE73DDA947FF72173C362A9D2512EA`.

Test thực tế kiểm tra 319 file không phải target giữ nguyên, archive export có exact hash, re-extract thành công, target có metadata mong đợi và mọi pristine hash vẫn không đổi. Fixture/tool không được commit; nếu thiếu hoặc sai hash thì gate phải skip/fail rõ ràng theo policy hiện hữu, không tạo PASS giả.

## Giới hạn tuyên bố

Đây là bằng chứng recovery ở application/file transaction boundary, không phải bảo đảm chống mất điện ở mọi filesystem/hardware, không chứng minh Windows package manager rollback ngoài contract hiện có, không chứng minh Supabase/Stripe production, và không xác nhận tương thích runtime/in-game. Sản phẩm vẫn dừng ở standalone `.ab`/`.acv`; không discover, patch, install hoặc điều khiển game.

## Kết quả xác minh tại checkpoint PLAN 99

- Gate trực tiếp `Coverage=Plan99`: 11 PASS, 0 FAIL, 0 SKIP; ma trận typed có 13 case.
- Full Debug: 1.316 PASS, 0 FAIL, 14 conditional skip. Full Release: 1.316 PASS, 0 FAIL, 14 conditional skip. Trong mỗi run, 12 skip là PostgreSQL do connection chỉ được cấp cho gate database cô lập; hai skip còn lại là test tạo symbolic link cần quyền Windows.
- Gateway chạy lại với PostgreSQL 17.6 loopback cô lập: 143 PASS, 0 FAIL, 0 SKIP. Security Matrix: 12/12 PASS. Release Security Review: 8/8 PASS trên public staging không có PDB/source/private fixture.
- App Release và Gateway Release build: 0 warning, 0 error; XAML được compile trong App build. Dependency audit không có finding; SPDX SBOM 68 package version khớp; supply-chain policy PASS với 22 lock file.
- Real DirectXTex, Product Gate C và File-only Gate đều PASS khi dùng approved `texconv.exe` và output root cô lập. Hai real archive gate xác minh re-extract; File-only Gate giữ nguyên 319 non-target file và chỉ thay `texture/hud/pointer.dds`.
- Secret scan PASS trên 686 file của source/build/log/crash trong Security Matrix và 89 file của public artifact trong Release Review. Artifact exposure scan xác nhận 0 debug symbol, source file, private mapping và proprietary fixture.

Container PostgreSQL, output gate và public staging đều bị xóa sau test. Các hash `015.ab`, `015.keydat`, `acv.exe` và approved `texconv.exe` sau suite khớp chính xác các baseline ở trên. Conditional skip không được đổi thành PASS; database boundary đã được chạy lại riêng không skip, còn hai symlink case vẫn phụ thuộc quyền tạo symbolic link của Windows.
