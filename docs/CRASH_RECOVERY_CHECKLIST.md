# Checklist crash/recovery — PLAN 99

Checklist này dùng khi thay đổi extract, scan, Apply, build, pack, export, cache template mã hóa, publish template, updater, workspace hoặc transaction server.

## Trước khi chạy

- Xác minh working tree và exact HEAD được phê duyệt.
- Xác minh private fixture/tool bằng exact SHA-256; chỉ làm việc trên working copy cô lập.
- Không bật fault hook bằng environment variable hoặc cấu hình production. Observer/checkpoint phải internal, test-only và mặc định không có tác dụng.
- Không dùng `Thread.Sleep`, delay ngẫu nhiên hoặc polling timing để quyết định điểm crash.

## Với mỗi fault point

- Ghi rõ thao tác, checkpoint, cơ chế mô phỏng và trạng thái mong đợi.
- Hash byte đã commit trước fault; sau fault phải bằng đúng hash cũ nếu commit chưa xảy ra.
- Không coi `.tmp`, `.partial`, `.stage-*`, candidate, backup hoặc cây extract dở dang là output hợp lệ.
- Cleanup chỉ exact operation path; không đi theo reparse và không xóa workspace/project khác.
- Retry phải hoặc hoàn tất sạch từ nguồn đã xác minh, hoặc trả conflict/idempotent replay nếu commit đã xảy ra.
- Không log raw archive content, plaintext cache, bearer token, payment secret hoặc private absolute path.

## Gate bắt buộc

```powershell
dotnet test AuditionModStudio.slnx -c Debug
dotnet test AuditionModStudio.slnx -c Release
powershell -NoProfile -File scripts/Invoke-SecurityTestMatrix.ps1 -Configuration Release
powershell -NoProfile -File scripts/Invoke-ReleaseReview.ps1 -Configuration Release
```

Ngoài ra chạy trực tiếp `Plan99CrashRecoveryMatrixTests`, các test fault-point được liệt kê trong `CRASH_RECOVERY_EVIDENCE.md`, gate archive thật và PostgreSQL concurrency/idempotency. Báo cáo phải nêu PASS/FAIL/SKIP thật, exact pristine hashes, residue còn lại và blocker production; không làm yếu acceptance criteria để đạt PASS.
