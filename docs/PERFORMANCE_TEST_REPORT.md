# Báo cáo kiểm thử hiệu năng — PLAN 100

## Phạm vi có thẩm quyền

Roadmap quy định nguyên văn cho PLAN 100: “Measure archive scan, thumbnail generation, 6000×1801 resize/BC3 encode, memory usage, large batch operations and UI responsiveness.” PLAN này chỉ bổ sung lớp kiểm thử và bằng chứng; không thay đổi production pipeline, định dạng project/archive, security boundary hoặc file-only product boundary.

Roadmap không đặt ngưỡng latency, throughput, working set, allocation, startup time hay UI frame time. Vì vậy mọi số đo trong báo cáo này có trạng thái **INFORMATIONAL**. Điều kiện PASS cứng là:

- kịch bản hoàn tất đúng và giữ nguyên oracle về nội dung, metadata, concurrency và immutable fixture;
- không có regression, secret leak, path escape hoặc nới lỏng resource/security policy;
- test có thể chạy lại bằng cấu hình và fixture được mô tả dưới đây.

Không suy diễn ngưỡng PASS/FAIL số học và không dùng số đo local làm cam kết SLA hay cấu hình tối thiểu.

## Checkpoint và môi trường đo

| Thuộc tính | Giá trị |
|---|---|
| Ngày ghi nhận | 2026-08-13 |
| Commit nền | `6d6cd60f972be51d96ca20fde7a8155032b1babc` |
| Cấu hình đo | Release, x64, không gắn debugger |
| Hệ điều hành | Windows 11 Pro 64-bit, 10.0.26200 (build 26200) |
| CPU | Intel Core i7-12700KF, 12 core/20 logical processor |
| RAM khả dụng theo OS inventory | 16.602.280 KiB, khoảng 15,83 GiB |
| .NET | SDK 10.0.302; host/runtime 10.0.10 x64 |
| Windows App SDK | 2.3.1 |
| `acv.exe` | 1.775.616 byte; SHA-256 `6A52C808D7E5A59EB41E43D86A32E78067F424C8531E34981887F093E81547D3` |
| `texconv.exe` | 966.480 byte; SHA-256 `DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06` |
| Archive fixture | `015.ab`, 12.371.872 byte; SHA-256 `3C4272BDDDA815B9F9E832D5FCF9EE10E2417301726C895CDB312DFF675C6081` |
| Keydat companion | 4.168 byte; SHA-256 `78F2910819D155CFBF0E366A80FEA4F40126BEA8C201E25EBDB0E0F956E2297F` |

Kết quả định lượng dưới đây là một snapshot trên máy này. Chúng có thể đổi theo tải hệ thống, page cache, antivirus, filesystem, JIT, runtime và tốc độ ổ đĩa.

## Phương pháp

- Thời gian dùng `Stopwatch` monotonic. Mỗi scenario ghi input, số lần chạy và trạng thái cache nếu xác định được.
- Chỉ báo p95 khi có ít nhất 20 sample; không báo p99. Với số sample nhỏ, báo min/median/max hoặc tổng thời gian một lần chạy.
- “First pass” không được gọi là cold: OS file cache không được kiểm soát. Các lần sau được gọi là “repeated”, không khẳng định warm-cache tuyệt đối.
- `GC.GetTotalAllocatedBytes` là quan sát allocation managed của toàn process trong cửa sổ đo; có thể gồm xUnit/background work. `WorkingSet64` chỉ là chênh lệch hai endpoint, không phải peak working set. Số âm là nhiễu/GC hợp lệ, không phải bộ nhớ âm.
- Native child-process peak của DirectXTex, UI frame time/INP và peak working set thực chưa được instrument nên ghi **NOT VERIFIED**.
- Kịch bản archive/DDS dùng đúng service production và working copy. Không benchmark bằng mock cho claim về ACV hoặc DirectXTex.
- Các fixture pristine được hash trước/sau. Output benchmark chỉ nằm trong thư mục tạm và được dọn sau khi kiểm tra.

## Kết quả scenario

### Scan, thumbnail, ảnh lớn và batch orchestration

| Scenario | Input/lần chạy | Kết quả thời gian | Allocation/working-set endpoint | Kết quả đúng |
|---|---|---|---|---|
| `archive-header-scan` | 320 file, 310 DDS, 11 thư mục, 256.486.544 logical byte | first pass 28,911 ms; repeated n=5: min 13,874, median 15,445, max 26,885 ms; 20.070,571 DDS/s theo median | first pass 1.702.360 byte; WS delta -2.281.472 byte | 310/310 header đọc được; archive hash giữ nguyên |
| `thumbnail-generated` | `pointer.dds` 21.120 byte → thumbnail 192 px; n=1 | 80,090 ms | 913.904 byte; WS delta +102.400 byte | source `Generated`, DDS nguồn và working copy giữ nguyên hash |
| `thumbnail-memory-hit` | cùng key; n=25 | min 0,026, median 0,037, max 0,643, p95 0,095 ms | nằm trong cửa sổ scenario thumbnail | 25/25 source `Memory` |
| `thumbnail-disk-hit` | cache instance mới; n=5 | min 1,193, median 1,706, max 4,854 ms | nằm trong cửa sổ scenario thumbnail | 5/5 source `Disk` |
| `thumbnail-single-flight` | 16 request đồng thời, key mới | tổng 50,130 ms | nằm trong cửa sổ scenario thumbnail | đúng 1 `Generated`; mọi request thành công |
| `resize-6000x1801` | RGBA 6.000×1.801 → 3.000×901, Linear; n=3 sau JIT warm-up nhỏ | min 166,264, median 171,389, max 205,032 ms | 97.320.240 byte tổng; WS endpoint lớn nhất +46.723.072 byte | đủ kích thước; source byte-identical |
| `adjustment-6000x1801` | RGBA 6.000×1.801, brightness +0,01; n=3 sau JIT warm-up nhỏ | min 775,964, median 795,003, max 805,309 ms | 389.066.784 byte tổng; WS endpoint lớn nhất +129.687.552 byte | đủ kích thước; source byte-identical |
| `background-batch-320` | 320 orchestration job, concurrency=4, barrier xác định; n=1 | tổng 15,545 ms; first-running 9,979 ms; 20.585,132 job/s | 603.208 byte; WS delta -90.112 byte | 320/320 success; max concurrency đúng 4 |

Mọi hàng trên là **INFORMATIONAL**. Scenario batch đo scheduling/orchestration, không giả làm throughput xử lý DDS/archive. Correctness về cancellation/backpressure tiếp tục do các test Background Task Manager hiện có kiểm tra.

Allocation của simple adjustment là vùng cần theo dõi: khoảng 129,7 MB cho mỗi lần chạy trong snapshot này. PLAN 100 không thay production implementation vì roadmap không có ngưỡng bị vi phạm, resource policy hiện hữu vẫn được giữ và toàn bộ correctness gate PASS. Pooling/tiling chỉ nên triển khai trong PLAN được phê duyệt kèm profiling peak/ownership rõ ràng; tối ưu suy đoán có thể làm tăng rủi ro giữ dữ liệu hoặc alias buffer.

### DirectXTex và BC3 6.000×1.801

| Scenario thực | Kết quả | Giới hạn claim |
|---|---|---|
| Preview/import corpus DDS | 52/52 file, bốn format BC1/BC3/RGBA8/BGRA8, file lớn nhất 4.000×4.000; tổng 4,55 giây | Một lần chạy corpus; không phải per-format percentile |
| `tn_coby_logo.dds` 6.000×1.801, DXT5/BC3, 1 mip | Match Original + validate + decode-back hoàn tất trong 0,17 giây | Timer bao quanh toàn `MatchAsync`; không tuyên bố đây là latency riêng của encode hoặc peak memory native |

Kịch bản giữ kích thước non-power-of-two 6.000×1.801 và exact metadata contract. DirectXTex chỉ chạy qua boundary hiện hữu, absolute executable path, pinned SHA-256, redirected I/O, isolated working directory và timeout hữu hạn.

### Full archive file-only pipeline

| Pha | Thời gian |
|---|---:|
| Chuẩn bị immutable template working copy | 14.132 ms |
| Extract bằng ACV Tool 5 | 6.556 ms |
| Scan | 1.146 ms |
| Apply + validate DDS | 243 ms |
| Validate project | 491 ms |
| Build/pack | 15.272 ms |
| Export cancellation + clean retry | 1.248 ms |
| Re-extract + logical verification | 7.429 ms |
| Tổng các pha | 46.517 ms |

Artifact cuối có 75.476.216 byte, SHA-256 `4F253B718CADF585F7150B9F9C2710A6B2B50129E77545ABDDB450C93A91B0E7`. Re-extract xác nhận đúng 320 file: chỉ `texture/hud/pointer.dds` thay đổi thành SHA-256 `234607B9C9F5EB747A85D5857B9C716C7A181931BECCE88B1D947313E7ADD368`; 319 file ngoài target byte-identical. Target giữ 164×128, BC3, 1 mip, interpolated alpha và legacy header semantics. Working template sau prepare có SHA-256 `0FF2616F177C27BBAEA4DDBB1E19DB918AC7D766E2A2DFD4C325607B3A07E7E0` và không bị build sửa trực tiếp.

Pha export gồm cả nhánh cancel và retry nên không được diễn giải là throughput của một lần export sạch. Container output không được so golden hash vì ACV container có thể không deterministic; oracle là re-extracted logical inventory và exact file hash.

## UI responsiveness và startup/project-load

UI không bị tự động hóa tương tác trong PLAN 100. Bằng chứng tự động chỉ xác nhận contract-level responsiveness:

- 320 task được dispatch bất đồng bộ, first-running event xuất hiện và concurrency bị chặn đúng 4;
- thumbnail cache có memory/disk hit và single-flight, tránh decode trùng cho cùng key;
- archive/image operation tiếp tục ở sau service boundary và Background Task Manager có correctness test về progress/cancel/backpressure;
- metadata scan là header-only và thumbnail được sinh theo nhu cầu.

Manual WinUI interaction, frame timeline, input latency, startup cold/warm và project-load end-to-end trên GUI là **NOT VERIFIED**. Không có claim “UI luôn mượt” hoặc startup SLA. Việc đo GUI đáng tin cậy cần một PLAN riêng với ETW/Windows Performance Recorder hoặc UI automation đã kiểm soát cache và machine load.

## Bảo mật, bất biến và non-goals

- Không gọi shell/CMD, không thêm executable tùy ý, không log secret/raw DDS content và không nới path/resource limits.
- Không mutate pristine `015.ab`, `015.keydat`, `acv.exe`, DDS corpus hoặc global archive template.
- Không thêm game discovery/install/backup/patch/launch/login/process/registry/runtime automation. Điểm cuối vẫn là standalone `.ab`/`.acv`.
- Không đưa archive/tool/fixture vào public artifact. Hash trong báo cáo là integrity evidence, không chứng minh provenance hoặc quyền phân phối.
- Gateway, PostgreSQL, Stripe, updater, installer, network/CDN và game FPS/runtime nằm ngoài phạm vi performance của PLAN 100.
- Production signer/HSM/timestamp, live Supabase/Stripe, production CDN/feed, ACV redistribution và game-runtime compatibility vẫn **NOT VERIFIED**.

## Tối ưu và Before/After

Không có thay đổi production performance trong PLAN 100. Do roadmap không có hard numeric target và không có correctness/resource gate nào FAIL, Before/After là **N/A**. Các thay đổi chỉ gồm executable measurement harness, trait liên kết bằng chứng real gate và tài liệu. Full Debug/Release cùng security/real gates sau thay đổi đều PASS, nên không có regression do tối ưu cần che giấu.

## Kết quả regression và gate trước checkpoint commit

| Gate | Kết quả |
|---|---|
| Targeted `Coverage=Plan100` Release | 6 PASS, 0 FAIL, 0 SKIP |
| Full Debug, có PostgreSQL và real fixture | 1.332 PASS, 0 FAIL, 2 conditional SKIP |
| Full Release, có PostgreSQL và real fixture | 1.332 PASS, 0 FAIL, 2 conditional SKIP |
| Hai conditional skip | test tạo symbolic link cần quyền Windows; không đổi thành PASS |
| Gateway/PostgreSQL trong mỗi full run | 143 PASS, 0 FAIL, 0 SKIP |
| App x64 Debug/Release + XAML | build PASS, 0 warning, 0 error |
| Gateway Debug/Release | build PASS, 0 warning, 0 error |
| ACV Product Gate C + File-only + DirectXTex + DDS Matrix | 10 PASS, 0 FAIL, 0 SKIP |
| Crash/Recovery `Coverage=Plan99` | 11 PASS, 0 FAIL, 0 SKIP |
| Security Matrix | 12/12 PASS; secret scan 688 file |
| Release Security Review | 8/8 PASS; artifact exposure 0 finding; secret scan 89 file |
| `dotnet format` / `git diff --check` | PASS |
| NuGet vulnerability audit | PASS, không finding theo source hiện tại |
| SPDX SBOM | PASS, 68 package version duy nhất |
| Supply-chain policy | PASS, 22 lock file; proprietary redistribution blocked; GitHub Actions SHA-pinned |
| Source/docs secret scan riêng | PASS, 577 file |

Symlink skip là trạng thái môi trường đã biết, không phải bằng chứng symlink-safe trên máy này. Các test PostgreSQL được chạy thật qua PostgreSQL 17.6 loopback, không còn conditional skip. Gateway/PostgreSQL performance không thuộc exact PLAN 100 nên không có latency claim server.

## Fixture hash trước/sau

| Artifact | SHA-256 trước | SHA-256 sau suite | Trạng thái |
|---|---|---|---|
| `015.ab` | `3C4272BDDDA815B9F9E832D5FCF9EE10E2417301726C895CDB312DFF675C6081` | cùng giá trị | bất biến |
| `015.keydat` | `78F2910819D155CFBF0E366A80FEA4F40126BEA8C201E25EBDB0E0F956E2297F` | cùng giá trị | bất biến |
| `acv.exe` | `6A52C808D7E5A59EB41E43D86A32E78067F424C8531E34981887F093E81547D3` | cùng giá trị | bất biến |
| `pointer.dds` | `854189B91972C17C483C2434FC077B1F94AE73DDA947FF72173C362A9D2512EA` | cùng giá trị | bất biến |
| approved `texconv.exe` | `DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06` | cùng giá trị | bất biến |

## Hạn chế đã biết và technical debt

- Skia/native decode không thể bảo đảm hủy giữa một lời gọi native đang chạy; cancellation có hiệu lực ở boundary trước/sau.
- Ảnh lớn đồng thời tồn tại ở nhiều representation. PNG bridge làm tăng allocation và overhead của DDS encode; DirectXTex còn có child-process startup cost.
- Full selected texture materialize một UI bitmap. Cache miss của thumbnail có thể decode một preview lớn tạm thời trước khi resize; chỉ same-key single-flight đã được đo.
- ACV pack bị chi phối bởi external tool. Real archive timing nhạy với disk/page cache, antivirus và trạng thái hệ thống.
- Managed allocation process-wide và working-set endpoint không thay thế profiler peak. Peak native DirectXTex/Skia, sustained batch memory, cache eviction pressure và máy RAM thấp vẫn **NOT VERIFIED**.
- Manual GUI, Dark/Light/HighContrast visual review, keyboard/focus timing, responsive layout interaction, cold startup và project-open latency chưa được đo trong PLAN này.

Technical debt có bằng chứng rõ nhất là allocation của adjustment 6.000×1.801 và thiếu peak-memory telemetry. Việc xử lý tiếp cần PLAN được duyệt với profiler an toàn, ownership/lifetime analysis và correctness oracle; không được giảm resource/security limit để đổi lấy số đo đẹp.

## Trạng thái production không thay đổi

| Hạng mục | Trạng thái |
|---|---|
| Production IAM | **NOT VERIFIED** |
| Production HSM/KMS | **NOT VERIFIED** |
| Production private object storage | **NOT VERIFIED** |
| Production signing/timestamp | **NOT VERIFIED** |
| Production updater endpoint/CDN | **NOT VERIFIED** |
| Live Supabase/PostgreSQL/Stripe | **NOT VERIFIED** |
| Concrete external AI provider | **NOT IMPLEMENTED / NOT VERIFIED** |
| ACV/proprietary template/game asset redistribution rights | **NOT VERIFIED** |
| DirectXTex redistribution/notices in final commercial bundle | **NOT VERIFIED** |

## Kết luận

PLAN 100 có executable performance evidence cho toàn bộ phạm vi roadmap: archive scan, thumbnail generation/cache, ảnh 6.000×1.801, BC3 Match Original, memory observations, large batch orchestration và contract-level UI responsiveness. Không phát hiện correctness regression hoặc lý do có bằng chứng để sửa production code. Các số đo là baseline local, không phải hard gate; giới hạn về peak memory native, peak working set và GUI interaction được ghi rõ thay vì overclaim.
