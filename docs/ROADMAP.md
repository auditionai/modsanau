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
- Phạm vi PLAN 05: private fixture catalog, optional DDS sample metadata, canonical source location và copy-before-mutation trong isolated workspace. `tn_coby_logo.dds` không phải fixture bắt buộc; source of truth DDS là các file được scan từ working archive đã extract.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 06 — ACV Tool 5 Interactive Process Runner.
- Phạm vi PLAN 06: direct child process, redirected stdin/stdout/stderr, chunk parser, trusted AuditionVN profile, deterministic state/progress, timeout/cancellation, process-tree termination và artifact verification bằng fake process.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 07 — Keydat Lifecycle + ACV Tool Integrity.
- Phạm vi PLAN 07: centralized keydat derivation/status/copy/lifecycle; code-owned ACV Tool manifest; source/copy/pre-launch SHA-256 verification; structured integrity rejection và TOCTOU documentation.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 08 — Audition Archive Abstraction.
- Phạm vi PLAN 08: extension-independent archive descriptor; semantic extract/pack contract; explicit engine resolution; pristine-to-working verified copy; secure extracted path; ACV Tool 5 provisioning/keydat/runner orchestration; structured progress/error/cancellation/timeout.
- PLAN đã hoàn thành kiểm chứng tự động: PLAN 09 — Project Archive Workspace.
- Phạm vi PLAN 09: per-project randomized secure workspace lease; verified pristine-to-working copy; separate extracted/build output; template identity/version snapshot; atomic schema-v1 manifest; structured validation và isolated cleanup.
- PLAN đã hoàn thành kiểm chứng Gate A: PLAN 10 — Real Extract Integration POC With AuditionVN Selection.
- Phạm vi PLAN 10: real approved `acv.exe` + working copy `015.ab`; AuditionVN stdin automation; missing/existing-`PresentUnverified` keydat flows; bounded real stdout diagnostics; Unicode/space workspace; aggregate extract inventory; pristine hash verification và cleanup.
- PLAN kế tiếp theo đặc tả: PLAN 11 — Recursive Asset Scanner; không bắt đầu trước khi PLAN 10 được người dùng phê duyệt.

## Stage Gates

- Gate A: real archive extract và interactive AuditionVN selection.
- Gate B: real DDS roundtrip dựa trên metadata của chính target DDS; có thể dùng tập optional sample đa dạng, không phụ thuộc bắt buộc vào `tn_coby_logo.dds`.
- Gate C: replace, pack và kiểm thử trong Audition thật.

Không phát triển AI/store trước khi Gate A đạt. Archive/DDS pipeline chỉ được xem là đã chứng minh sau Gate C.

## Quy trình

Mỗi lần chỉ thực hiện một PLAN:

`PLAN → BUILD → TEST → BÁO CÁO → DỪNG`
