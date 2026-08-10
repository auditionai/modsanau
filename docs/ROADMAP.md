# Trạng thái roadmap

Nguồn đặc tả duy nhất cho thứ tự và acceptance criteria là `Audition_AI_Mod_Studio_MASTER_ROADMAP_V3.md`. Không sao chép hoặc làm yếu acceptance criteria tại đây.

## Trạng thái hiện tại

- PLAN đã hoàn thành: PLAN 01 — Repository & Solution Foundation.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 02 — Windows App Bootstrap + Administrator.
- Phạm vi PLAN 02: Generic Host/DI lifecycle, LocalAppData paths, file logging, startup validation, global exception handling, graceful shutdown và UAC manifest.
- Ngoài phạm vi: settings, fixture registration, ACV, DDS, Imaging, Mods, Projects, AI/Cloud và các security feature của PLAN sau.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 03 — App Paths & Secure Workspace Paths.
- Phạm vi PLAN 03: centralized managed directories, canonical relative-path validation, randomized isolated workspace, exclusive active marker, abandoned cleanup và reparse-point mitigation.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 04 — Settings System.
- Phạm vi PLAN 04: strongly typed non-secret JSON settings, schema v1, validation, atomic save, bounded backup, corruption recovery, concurrency và cancellation.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 05 — Real Sample Fixture Registration.
- Phạm vi PLAN 05: private fixture catalog, DDS expected metadata, canonical source location và copy-before-mutation trong isolated workspace.
- PLAN kế tiếp theo đặc tả: PLAN 06 — ACV Tool 5 Interactive Process Runner; chỉ bắt đầu sau khi PLAN 05 được người dùng phê duyệt.

## Stage Gates

- Gate A: real archive extract và interactive AuditionVN selection.
- Gate B: real DDS roundtrip với `tn_coby_logo.dds`.
- Gate C: replace, pack và kiểm thử trong Audition thật.

Không phát triển AI/store trước khi Gate A đạt. Archive/DDS pipeline chỉ được xem là đã chứng minh sau Gate C.

## Quy trình

Mỗi lần chỉ thực hiện một PLAN:

`PLAN → BUILD → TEST → BÁO CÁO → DỪNG`
