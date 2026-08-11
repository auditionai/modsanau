# Nền tảng bảo mật

Tài liệu bắt buộc chi tiết là `Audition_AI_Mod_Studio_SECURITY_CHECKLIST_V2.md`. File này ghi lại các quyết định nền tảng áp dụng từ PLAN 01.

## Trust boundary

Windows client và dữ liệu local không phải nguồn tin cậy cho hoạt động thương mại. Trusted backend phải quyết định entitlement, AI pricing, credit reservation/charge/refund và trạng thái payment.

Không được đưa vào client, source control, log hoặc crash report:

- AI provider secret;
- Supabase service-role key;
- payment/webhook secret;
- template master encryption key;
- private signing key.

Token người dùng cần lưu về sau phải đi qua Windows secure storage abstraction. Obfuscation, Native AOT, anti-tamper, hidden directory, đổi extension và keydat chỉ là hardening layer.

## File và process boundary

- Không sửa pristine/global archive template.
- Không commit commercial template, `acv.exe`, keydat hoặc extracted game assets.
- Project chỉ thao tác trên working copy trong validated isolated workspace.
- Validate và canonicalize untrusted path; chặn path traversal.
- Launch tool/native component bằng absolute trusted path; không ghép shell command từ input.
- ACV Tool 5 về sau phải redirect stdin/stdout/stderr và kiểm tra tool integrity.

## Release boundary

Release cuối cùng phải có signed binary, signed installer và signed/hash-verified update. Signing key nằm trong secure CI/HSM/certificate service, không nằm trong repository hoặc client.

Các biện pháp RLS, credit concurrency, payment webhook, template encryption, signing và supply-chain automation chỉ được triển khai ở PLAN được roadmap quy định. PLAN 01 không cung cấp implementation giả hoặc security theater.

## Elevation và logging từ PLAN 02

- Executable dùng Windows manifest chuẩn với `requestedExecutionLevel="requireAdministrator"` và `uiAccess="false"`.
- Không có self-relaunch, `cmd.exe`, PowerShell, giả lập hoặc bypass UAC.
- Toàn ứng dụng hiện chạy elevated theo yêu cầu sản phẩm; điều này làm tăng blast radius. Business/domain contract không phụ thuộc elevation để có thể tách `normal UI + elevated broker` về sau.
- Normal application data chỉ nằm dưới `%LocalAppData%\AuditionModStudio`, không nằm trong installation directory.
- Log rolling file có timestamp, level, structured properties và exception stack trace.
- Source code không được ghi password, token, API/payment/encryption/signing secret vào log. Thông báo lỗi cho người dùng không hiển thị stack trace.

## Path boundary từ PLAN 03

- Relative path không được nối trực tiếp. `IPathSecurity` bắt buộc canonicalize rồi xác nhận kết quả còn dưới approved root.
- Cấm `..`, `.`, absolute path, UNC path, drive switching, empty segment, Windows reserved name, trailing dot/space, alternate data stream và malformed filename.
- Unicode filename hợp lệ được chuẩn hóa Form C; khoảng trắng và tiếng Việt vẫn được hỗ trợ.
- Startup từ chối reparse point đã tồn tại trong managed path trước khi logger ghi file. Workspace create/resolve/cleanup kiểm tra lại reparse point; traversal lúc cleanup dùng top-level enumeration để không đi xuyên symbolic link/junction.
- Đây là mitigation thực dụng, không phải filesystem sandbox tuyệt đối. Validate-then-use vẫn có cửa sổ TOCTOU nếu local attacker có thể thay path đồng thời. Các operation nhạy cảm về sau phải kiểm tra lại ngay trước khi mở file; broker tương lai nên dùng handle-based/open-reparse-point policy và ACL chặt hơn.

## Administrator và LocalApplicationData

Policy PLAN 03 là application data thuộc Windows identity đang chạy process. `LocalApplicationData` là known folder per-user của current non-roaming user.

- UAC consent bằng cùng administrator account: elevated process vẫn thuộc cùng identity, nên root vẫn là LocalAppData của tài khoản đó.
- UAC credential prompt dùng administrator account khác: process elevated chạy bằng alternate credentials; LocalAppData có thể thuộc profile của administrator đó, không phải tài khoản đang sở hữu desktop ban đầu. Ứng dụng không tự đoán hoặc hard-code profile của desktop user.

Hệ quả hiện tại là project/log/settings có thể xuất hiện trong profile của alternate administrator. Đây là technical debt do yêu cầu toàn app `requireAdministrator`. Hướng dài hạn là UI chạy unelevated và chỉ privileged operation đi qua elevated broker có protocol/path allowlist; PLAN 03 không thay đổi yêu cầu elevation.

## Settings boundary từ PLAN 04

- `ApplicationSettings` chỉ chứa non-secret configuration. Model không có password, token, API key, payment secret, encryption key, signing key hoặc credential.
- JSON unknown property bị từ chối; enum dùng tên rõ ràng và integer enum bị cấm; không dùng polymorphic deserialization.
- Backend URL không được nhúng user-info/credential. Non-loopback backend bắt buộc HTTPS; HTTP loopback chỉ tạo validation warning cho local development.
- Internal `Cache`, `Temp`, `Workspaces` và `SecureTemplateCache` không thể bị override qua schema settings.
- External game/tool/project path chỉ được kiểm tra cấu trúc ở PLAN 04. Giá trị cấu hình local vẫn là untrusted input; module sử dụng sau này phải kiểm tra existence, integrity, reparse point và authorization ngay trước operation.
- Log chỉ ghi schema version, source, operation result và validation code/path; không serialize toàn bộ settings object.
- Save dùng temp file cùng `SettingsDirectory`, flush-to-disk và atomic replace/move. Backup chỉ có một thế hệ. Primary corrupt được giữ tại một evidence file hữu hạn khi backup hợp lệ được phục hồi.

## Test fixture boundary từ PLAN 05

- `acv.exe`, `015.ab`, `015.keydat`, extracted folder và `samples/private/` nằm ngoài source control.
- Fixture registration là test metadata, không phải production trust hoặc integrity policy.
- Test mutation chỉ diễn ra trên copy trong randomized secure workspace. Source được mở read-only và hash source/copy phải khớp sau copy.
- Registered relative path được canonicalize và kiểm tra reparse point trước khi mở.
- Không chạy `acv.exe`, không extract/pack archive và không parse DDS tại PLAN 05.

## Archive process boundary từ PLAN 06

- `AcvTool5Runner` chỉ chạy executable tuyệt đối nằm trong isolated working workspace; archive và extract directory phải resolve qua `IPathSecurity` dưới cùng root và không đi qua reparse point đã tồn tại.
- `IArchiveToolExecutionPolicy` là trust boundary bắt buộc trước launch. Từ PLAN 07, production policy kiểm tra trusted manifest, containment, reparse point, filename và SHA-256 thay vì chỉ allowlist đường dẫn.
- Không dùng shell, `cmd.exe`, PowerShell, `SendKeys`, mouse/keyboard simulation hoặc UI Automation. Arguments được truyền riêng qua `ProcessStartInfo.ArgumentList`.
- Stdout parser đọc theo chunk với buffer hữu hạn. Country selection lấy từ trusted `GameRegionProfile` và chỉ gửi một lần qua redirected stdin.
- Timeout/cancellation dùng process-tree termination để không bỏ mặc child process. Kết quả chỉ thành công sau khi kiểm tra progress marker và artifact workspace tương ứng.
- Log chỉ chứa operation/state/exit code/count; không log executable, archive hoặc asset path đầy đủ. Raw stdout/stderr được trả về dưới giới hạn dung lượng cấu hình để diagnostic nhưng không tự động dump vào log.
- Real proprietary `acv.exe`, archive và keydat không được chạy/sửa trong PLAN 06; integration protocol dùng fake child process. Keydat tiếp tục là runtime artifact, không phải secret hay DRM boundary.

## Keydat và tool integrity boundary từ PLAN 07

- Keydat là runtime companion artifact ACV Tool 5 có thể tự tạo từ country selection. Nó không phải password, encryption key, license secret hoặc DRM; ẩn/mã hóa keydat không thay thế server authorization, entitlement, template access control hay executable integrity.
- Mỗi workspace sở hữu writable keydat riêng. Derivation theo archive basename được tập trung trong `IKeydatService`; file có mặt chỉ được đánh dấu `PresentUnverified`, không giả định semantic validity.
- Keydat source chỉ được đọc từ trusted root đã canonicalize; working copy được hash sau copy và cleanup theo workspace. Không API nào nhận arbitrary destination hoặc xóa source/global fixture.
- Approved `acv.exe` được xác định bởi code-owned manifest gồm tool id, exact filename, approved flag và SHA-256. User settings/project content không thể cung cấp hash để tự whitelist executable.
- Integrity rejection có reason riêng cho missing file, outside location, reparse point, filename mismatch, hash mismatch, unapproved tool và invalid manifest. Mismatch luôn chặn launch.
- Tool source được verify, copy vào isolated workspace và verify lại. Runner hash working copy ngay trước launch. PE version được ghi nhận khi có nhưng thiếu version resource không làm fail nếu SHA-256 đúng.
- Hash verification không phải DRM và chưa xóa hoàn toàn TOCTOU `hash → replace → launch`. Handle-based execution binding, restrictive ACL và elevated broker vẫn là technical debt cho hardening về sau.

## Archive abstraction boundary từ PLAN 08

- Caller chỉ gửi semantic `Extract`/`Pack` qua `IAuditionArchiveService`; không thể cung cấp `-da`, `-ca`, country selection, shell arguments, `ProcessStartInfo` hoặc arbitrary executable path.
- Archive engine được resolve bằng explicit code-owned `EngineId`, không bằng nhánh extension rải rác. Extension trong descriptor không làm thay đổi trust policy.
- Pristine archive source được resolve dưới trusted root, kiểm tra reparse point, mở read-only và copy vào isolated `Working` qua temp + flush + SHA-256 verification. Existing working archive không bị overwrite âm thầm.
- Working archive, extracted directory và build output giữ ba trust/lifecycle role khác nhau. Extract output được canonicalize dưới `Extracted`, không nằm trong pristine source hoặc arbitrary user directory.
- ACV Tool 5 orchestration bắt buộc provisioning/integrity trước keydat/runner. Invalid keydat hoặc integrity failure chặn process launch; missing keydat vẫn được lower-level runner xử lý bằng trusted `GameRegionProfile`.
- Archive-level diagnostic là bounded structured summary; UI không nhận raw process object hoặc dùng stdout/stderr làm nguồn trạng thái chính.
- PLAN 08 chỉ dùng fake engine/runner/provisioning/keydat trong test, không chạy `acv.exe` thật và không extract/pack fixture proprietary.

## Project Archive Workspace boundary từ PLAN 09

- Project workspace luôn được cấp bởi `SecureWorkspaceService` dưới randomized managed `Temp\Workspaces`; `ProjectId` và `DisplayName` không được dùng trực tiếp làm path.
- Pristine source nằm ngoài writable lease, được canonicalize, kiểm tra reparse point, mở read-only và hash trước copy. Working archive giữ exact filename, được copy qua temp + flush + SHA-256 verification và không overwrite file đã tồn tại.
- Working, Extracted và BuildOutput là ba vùng riêng. Hai project dùng cùng template vẫn có archive, keydat tương lai và extracted tree writable độc lập.
- Manifest schema v1 chỉ chứa non-secret metadata và relative path. Không chứa executable path, credential, token hoặc encryption/signing key. Manifest chỉ được promote atomically sau khi workspace hoàn chỉnh.
- Failure/cancellation không trả workspace `Ready`; partial lease được cleanup an toàn. Validation báo corruption/missing/hash mismatch thay vì tự chữa hoặc tin local metadata.
- Random workspace ID và LocalAppData isolation là filesystem safety, không phải DRM hoặc bảo vệ tuyệt đối trước local Administrator. Restrictive NTFS ACL/broker hardening sâu hơn vẫn thuộc PLAN 74 và kiến trúc elevation tương lai.
- Folder `015\` do người dùng extract thủ công ở repository không nằm trong trust boundary production. PLAN 09 không chạy tool, không đọc extracted asset thật và không sửa proprietary fixture.

## Real extract boundary từ PLAN 10

- Real execution chỉ được phép sau khi SHA-256 của pristine `015.ab`, `015.keydat` sample và `acv.exe` khớp checkpoint; process chỉ nhận working copies trong randomized managed workspace.
- Production chain vẫn đi qua project workspace, semantic archive service, ACV engine, provisioning/integrity policy, keydat service và runner. Integration test không chạy executable từ arbitrary project/settings path.
- ACV Tool 5 thật buffer stdout trong lúc chờ stdin. Selection `1` luôn đến từ code-owned `GameRegionProfile.AuditionVietnam`; pre-seed/liveness fallback không nhận text từ user và vẫn dùng redirected stdin, không shell/UI automation.
- `PresentUnverified` không đồng nghĩa keydat được tool chấp nhận. Fixture thật vẫn yêu cầu country selection ở lần extract thứ hai dù generated keydat trùng sample; runner ưu tiên cho process tự hoàn tất rồi mới fallback hữu hạn để tránh deadlock.
- Raw stdout/stderr chỉ được giữ bounded trong result diagnostic và không tự ghi toàn bộ asset path vào log. Test report chỉ giữ counts, byte totals, protocol metadata và keydat hash/length.
- Generated keydat và extracted game assets chỉ tồn tại trong disposable workspace, không được stage/commit. Keydat vẫn không phải secret hoặc DRM boundary.
- PLAN 10 không loại bỏ TOCTOU/Administrator risk đã ghi nhận; tool hash được kiểm tra source, copy và ngay trước launch, còn hardening handle/ACL/broker thuộc PLAN sau.

## Asset scanner boundary từ PLAN 11

- Source production duy nhất là extracted directory thuộc `IProjectArchiveWorkspace`; cây extracted thủ công ở repository không được scanner contract nhận trực tiếp.
- Mọi entry được chuyển thành relative path, resolve lại qua `IPathSecurity` và kiểm tra reparse point. Scanner không recurse qua symbolic link/junction và fail toàn catalog khi một entry không đọc an toàn được.
- Domain catalog không lưu absolute path. Identity giữ normalized relative directory path cộng exact filename; duplicate theo Windows case semantics bị từ chối thay vì overwrite.
- SHA-256 được tính streaming, hỗ trợ cancellation; hash là content identity/diagnostic, không phải chữ ký hay trust proof. Scanner không log danh sách tên asset.
- Enumeration/validation/open vẫn có cửa sổ TOCTOU trước local attacker có quyền ghi workspace; handle-based traversal là hardening tương lai.

## Real repack boundary từ PLAN 12

- Real pack chỉ mutate `Working\015.ab` trong randomized project workspace và chỉ đọc extracted tree của workspace đó. Pristine archive/tool/keydat không được mở để ghi; repacked archive chỉ được re-extract từ một disposable trusted source copy.
- Success không dựa riêng vào exit code. ACV Tool 5 thật trả code `1` sau pack thành công; policy chỉ chấp nhận code này cho Pack khi có `Packing:` progress, keydat không invalid và archive tồn tại/non-empty. Unexpected exit code, thiếu progress hoặc thiếu artifact vẫn fail và không được coi là promoted build.
- Existing `PresentUnverified` keydat và missing keydat đều có thể dẫn tới country prompt. Selection luôn lấy từ code-owned `GameRegionProfile.AuditionVietnam`, không từ user input.
- No-edit logical integrity được xác minh bằng identity, byte size, kind và SHA-256 của toàn bộ asset sau re-extract. Archive binary hash được phép khác vì packing representation không phải logical source of truth.
- PLAN 12 chưa tạo immutable/atomic `BuildOutput`; pack hiện mutate working archive. Promotion/rollback cho output phát hành vẫn là lifecycle cần triển khai ở PLAN phù hợp.

## DDS parser boundary từ PLAN 13

- DDS là binary input không tin cậy. Reader xác minh magic `DDS ` trước khi parse, sau đó xác minh legacy header size 124, pixel-format size 32 và DX10 header nếu có.
- File chỉ được mở `FileMode.Open` + `FileAccess.Read`; production read không hash payload, không ghi, không `OpenOrCreate` và không load toàn file.
- Parser chỉ cấp phát buffer header cố định 148 byte. Width, height, depth, mip count và array size không được dùng để cấp phát bitmap/payload; arithmetic offset có giới hạn và field little-endian được đọc từ span đã kiểm tra length.
- Malformed/truncated/missing/cancelled/I/O failure trả reason và error code có cấu trúc; UI không cần parse exception string. Unknown FourCC/DXGI không là process crash.
- Reader không dùng unsafe code hoặc native DLL. Integration gate hash toàn bộ DDS trước/sau chỉ trong test để chứng minh tính read-only; production không gánh chi phí này.

## DirectXTex evaluation boundary từ PLAN 14

- Harness không resolve `texconv.exe` từ `PATH`, current directory hoặc project content. Descriptor code-owned pin exact filename, version và SHA-256 của Microsoft DirectXTex `may2026` x64.
- Tool source phải là absolute path, không qua reparse point; binary được hash, copy và hash lại trong randomized secure workspace trước launch. Arguments dùng `ArgumentList`; shell bị tắt.
- DDS input được preflight bằng PLAN 13 metadata reader; PNG input được kiểm tra signature/IHDR. File size, pixel count, target dimension và mip count có upper bound trước khi native process có thể allocate.
- Input/output bị confine trong `ISecureWorkspace`; output chỉ được tạo dưới `BuildOutput`. Existing output không bị overwrite âm thầm.
- Process có timeout/cancellation, kill process tree và diagnostic capture hữu hạn. Malformed/preflight failure không launch native tool.
- Hash pinning chưa loại bỏ hoàn toàn TOCTOU/DLL-load risk và không thay thế Authenticode/supply-chain review. Harness không được expose thành production UX.

## DDS preview boundary từ PLAN 15

- Preview request chỉ nhận secure workspace và relative DDS path; không nhận executable/DLL path từ user hoặc project. Composition root cung cấp absolute packaged-tool location, còn descriptor PLAN 14 tiếp tục pin filename/version/SHA-256 và copy-verify trước launch.
- `IDdsMetadataReader` luôn chạy trước native decode. Chỉ known 2D, non-cubemap, single-array resources được chấp nhận; input bytes, dimensions, pixel count và encoded PNG bytes dùng `DdsPreviewResourcePolicy` tập trung. Header độc hại bị reject trước native allocation.
- Mỗi operation ghi vào `BuildOutput/DdsPreview-<random-id>`, không ghi PNG cạnh DDS source và không dùng global filename. PNG được kiểm tra IHDR/dimensions trước khi trở thành immutable memory DTO; temp operation directory được cleanup trong `finally`.
- Cancellation khác failure; external process có finite configurable timeout và kill process tree. Preview result chỉ trả stable diagnostic code, không chuyển raw stdout/stderr hoặc proprietary filename cho UI.
- Preview full-resolution giữ PNG bytes trong RAM; giới hạn hiện tại giảm rủi ro allocation nhưng chưa phải streaming/thumbnail cache. SHA pinning vẫn không loại bỏ hoàn toàn local Administrator, TOCTOU, DLL search-order hoặc release supply-chain risk.

## DDS encoder boundary từ PLAN 16

- Encoder nhận internal immutable RGBA8 buffer và explicit settings, không nhận arbitrary PNG/tool/destination absolute path. Stride, buffer length, dimensions, pixel count, mip chain, output estimate và alpha compatibility được kiểm tra trước khi ghi bridge hoặc launch process.
- Tool path tiếp tục do composition root cung cấp và đi qua exact PLAN 14 filename/version/hash boundary. Command dùng `ArgumentList`, `--`, no shell; timeout/cancellation tiếp tục kill process tree và raw diagnostics không đi vào encoder result.
- PNG bridge và native output dùng operation ID random trong `Working`/`BuildOutput`. Output DDS chỉ được atomic move tới relative path đã canonicalize sau khi `IDdsMetadataReader` xác minh dimensions, format, header, mip count và 2D resource shape. Existing output không bị overwrite.
- Legacy+sRGB và BC1+full-alpha bị từ chối thay vì hạ cấp âm thầm. Encoder không sửa input image, real target DDS, extracted DDS hoặc archive.
- RGBA buffer và temporary compressed PNG vẫn có peak-memory cost theo full-resolution input. Policy 512 MiB/100 triệu pixel/output estimate giảm rủi ro nhưng không thay thế process isolation, strict ACL, Authenticode và release supply-chain hardening tương lai.

## Match Original boundary từ PLAN 17

- Target DDS chỉ được resolve bằng relative path trong `ISecureWorkspace`; metadata reader mở read-only. Service không nhận tool path, trust hash hoặc absolute output path và không overwrite target.
- Profile chỉ được derive sau metadata validation. Unsupported format/resource/color/mip profile bị reject; không downgrade BC7→BC3, cubemap→2D hoặc BC1→BC3 để né incompatibility.
- Replacement RGBA phải đúng dimensions target; không cấp phát resize buffer hoặc stretch âm thầm. BC1 semi-alpha bị reject trước encoder.
- Output tiếp tục đi qua encoder isolation, resource limits, pinned tool, atomic promotion và metadata post-validation. Match report dùng strongly typed booleans; raw process diagnostics và proprietary path không đi vào UI contract.
- Structural match không chứng minh byte identity, gameplay compatibility hoặc archive safety. Replace/rollback/archive validation vẫn thuộc PLAN sau; local Administrator/TOCTOU/supply-chain risks đã ghi nhận vẫn còn.

## DDS validation boundary từ PLAN 18

- Validator chỉ nhận relative path trong `ISecureWorkspace`; target và candidate được canonicalize, kiểm tra reparse point và mở read-only qua `IDdsMetadataReader`.
- Validation không tin metadata do encoder trả về. Candidate được reopen từ filesystem, sau đó so format, dimensions, effective mip count, header, meaningful DX10 color space và resource shape với target.
- BC7, volume, array và cubemap tiếp tục bị reject có cấu trúc. Không có fallback format/header/mip và không suy diễn legacy `Unknown` thành sRGB.
- Validator không copy, move, delete hoặc overwrite target/candidate. Replacement extracted DDS chưa tồn tại trong PLAN 18; caller tương lai chỉ được replace sau `Succeeded=true`.
- Metadata-only validation dùng header buffer hữu hạn, không decode payload hoặc cấp phát theo dimensions không tin cậy. Nó không chứng minh pixel fidelity, byte identity, game compatibility hay archive safety; các gate đó thuộc PLAN sau.
