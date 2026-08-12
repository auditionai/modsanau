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

## File-only product boundary

- Sản phẩm chỉ mutate isolated project workspace và user-selected export destination; không discover, validate,
  mutate, backup hoặc restore Audition installation/game directory.
- Không đọc registry/launcher config để tìm game, không launch/login/automate game và không coi in-game
  observation là security/release gate.
- Boundary này thắng mọi legacy/future wording xung đột. App distribution/update là hợp lệ nhưng không được dùng
  làm đường vòng để discover, install, patch hoặc launch Audition game/mod.
- Export path là machine-local untrusted input. Nó không cấp authority cho game/mod/template/region/engine và
  phải được canonicalize, kiểm tra filename/extension/collision/access/reparse trước mọi write.
- Existing export chỉ được thay với explicit overwrite policy và transactional promotion; failure phải giữ bytes
  cũ, cleanup temp và trả structured result.

## Release boundary

Release cuối cùng phải có signed binary, signed installer và signed/hash-verified update. Signing key nằm trong secure CI/HSM/certificate service, không nằm trong repository hoặc client.

Các biện pháp RLS, credit concurrency, payment webhook, template encryption, signing và supply-chain automation chỉ được triển khai ở PLAN được roadmap quy định. PLAN 01 không cung cấp implementation giả hoặc security theater.

## Elevation và logging từ PLAN 02

- Executable dùng Windows manifest chuẩn với `requestedExecutionLevel="requireAdministrator"` và `uiAccess="false"`.
- Không có self-relaunch, `cmd.exe`, PowerShell, giả lập hoặc bypass UAC.
- Toàn ứng dụng hiện chạy elevated theo yêu cầu sản phẩm; điều này làm tăng blast radius. Business/domain contract không phụ thuộc elevation để có thể tách `normal UI + elevated broker` về sau.
- Product file-only hiện không còn cần ghi game installation, vì vậy `requireAdministrator` là technical debt cần
  dedicated security/runtime review. Không thay manifest/elevation policy trong PLAN 51.
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

## Apply texture boundary từ PLAN 47

- Apply chỉ nhận normalized `ModRelativePath` và exact retained project workspace; không nhận absolute output path, executable path hoặc command text từ UI.
- Target/candidate/backup/history assets đều resolve qua `ISecureWorkspace` và nằm trong `Extracted` hoặc `BuildOutput` của cùng workspace. Pristine/global archive không được mở.
- DDS candidate phải Match Original và qua validation độc lập trước atomic replacement. Project save failure/cancellation rollback target bytes và asset snapshots chưa commit.
- UI không gọi filesystem, DDS encoder, archive tool hay `acv.exe`; progress/cancel đi qua Background Task Manager. Diagnostic gửi UI là code/message hữu hạn, không chứa path tuyệt đối hoặc raw tool output.

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

## Image import boundary từ PLAN 20

- Import chỉ nhận absolute user-selected local path, canonicalize bằng `Path.GetFullPath`, yêu cầu regular existing file và reject reparse point trên source/ancestor. Source luôn mở `FileMode.Open`, `FileAccess.Read`; service không ghi temp/output cạnh source và không mutate file.
- PNG/JPEG/WebP/BMP được nhận diện từ signature và đối chiếu với format do `SKCodec` nhận diện; extension không được tin. Unsupported, empty, truncated, invalid và decode failure trả enum + stable diagnostic code, không chuyển exception/raw metadata cho UI.
- File size được chặn ở 512 MiB. Codec chỉ probe metadata trước; dimension tối đa 16384, pixel tối đa 100 triệu và decoded RGBA tối đa 512 MiB được kiểm tra bằng checked 64-bit arithmetic trước pixel allocation. Đây giảm decompression-bomb risk nhưng native decoder parsing vẫn là attack surface.
- Decoder `SkiaSharp` 4.150.1 được pin tập trung, license MIT, có native Skia runtime. Release cần dependency provenance, vulnerability scan, RID inventory và native-binary integrity/signing review; package pin không tự chứng minh supply-chain safety.
- Service stateless, không dùng temp/global filename nên concurrent import độc lập. Cancellation được kiểm tra trước I/O, sau signature/probe, trước/sau decode và trước model promotion; native `SKCodec.GetPixels` không hỗ trợ mid-call cancellation tức thời.
- Internal pixels là immutable managed RGBA8 straight-alpha, không chứa EXIF blob. Native stream/codec/bitmap/color-space objects được dispose deterministically. PLAN 20 không resize, replace DDS, pack archive hoặc launch game.

## Image resize boundary từ PLAN 21

- Resize request không có file/tool/native-library path. Input là validated immutable `InternalImage`; target dimensions, mode, filter, alignment và crop rectangle vẫn bị coi là untrusted và được validate trước native allocation.
- Target dùng chung `ImageImportResourcePolicy`: positive dimensions tối đa 16384, 100 triệu pixel và 512 MiB RGBA. Pixel/stride/byte arithmetic dùng checked integer/64-bit; invalid enum, crop overflow/out-of-bounds và padding canvas quá nhỏ trả stable structured failure.
- Source được copy vào per-operation premultiplied native bitmap; destination, canvas và color-space objects chỉ sống trong operation và được dispose deterministically. Output được unpremultiply/copy thành immutable managed `InternalImage`, không giữ reference tới native buffer đã dispose.
- Service stateless, không dùng shared bitmap/temp filename nên concurrent requests độc lập. Cancellation được kiểm tra trước geometry/allocation, trước và sau native draw, trước output promotion; native draw không hỗ trợ interrupt giữa call.
- Resize giữ cả source, native source/destination và managed output tại peak; policy hạn chế nhưng chưa thay thế memory-pressure telemetry. SkiaSharp native runtime/provenance risk đã ghi nhận ở PLAN 20 tiếp tục áp dụng.

## Crop/transform state boundary từ PLAN 22

- Geometry request không nhận path, stream, native pointer, UI object hoặc executable. State chỉ chứa immutable numeric/value data và image dimensions; geometry update O(1), không copy/cấp phát pixel buffer và không gọi native decoder/resizer.
- Mọi crop/viewport/point/zoom/pan/scale/translate/aspect/constraint input reject NaN/Infinity và invalid ranges. Crop bị clamp theo explicit policy; zero/outside/minimum-size crop, invalid zoom/scale/rotation/viewport và source-dimension mismatch trả structured failure.
- Canonical crop là normalized continuous state. Pixel conversion duy nhất dùng floor left/top, ceil right/bottom và checked/clamped bounds, giảm off-by-one và repeated-quantization drift.
- Projection matrix được derive từ immutable state theo transform order documented và inverse chỉ được dùng khi invertible. Service stateless, không cần UI thread và concurrent calculations không chia sẻ mutable state.
- PLAN 22 không execute arbitrary transform code, không resample, không tạo file và không log user geometry/metadata. Pixel execution duy nhất là strongly typed request translation tới validated PLAN 21 Manual Crop service.

## Image adjustments boundary từ PLAN 23

- Adjustment request chỉ chứa validated immutable `InternalImage` và numeric settings; không nhận path, stream, native pointer, UI object, executable hoặc DDS/archive metadata.
- Mọi parameter phải finite và nằm trong range contract. NaN, Infinity và out-of-range trả structured failure; channel overflow bị centralized clamp/quantize, không wrap byte.
- Dimensions, pixel count và decoded bytes được kiểm lại bằng `ImageImportResourcePolicy` với checked arithmetic trước output allocation. Neutral operation reuse source; non-neutral operation không mutate source và chỉ promote output sau khi hoàn tất.
- Managed loop kiểm tra cancellation theo row/column và không trả partial image. Service không có static mutable state nên concurrent request độc lập và deterministic với cùng input/settings/version.
- Sharpen/blur cần thêm full-image buffer và có peak-memory cost; policy hiện giảm allocation abuse nhưng chưa có memory-pressure telemetry hay pooled-buffer budget. Không filesystem/process/native dependency mới được thêm trong adjustment engine.
- Math RGB chạy trên sRGB channel và hidden RGB của transparent pixel vẫn được adjust. Alpha giữ exact cho mọi operation trừ opacity explicit; đây là contract pixel, không phải color-management/ICC hoặc compositing security boundary.

## Edit history boundary từ PLAN 24

- History là local in-memory state, không đọc/ghi filesystem, không chạy process, không deserialize command, không chứa secret và không biết UI thread/Dispatcher.
- Editor state và operation kind là strongly typed. Invalid state/dimension, enum, options, transaction transition và capacity failure trả structured result; caller không parse exception text.
- Revision và memory arithmetic dùng checked 64-bit operation. Revision không reuse sau branch invalidation; failed push/commit không advance revision hoặc mutate committed stacks.
- Mỗi editor có session riêng. Private lock serialize push/undo/redo/transaction/checkpoint/clear để concurrent caller không corrupt stack; immutable snapshots không expose mutable collection.
- Entry count và estimated memory đều bounded. Unique immutable image buffer chỉ tính một lần theo reference identity; oldest undo entry được evict trước và current state không bị evict. Entry không thể vừa budget bị reject atomically.
- Memory estimate bao gồm exact RGBA pixel bytes và fixed entry overhead, chưa bao gồm toàn bộ GC/object overhead. Đây là resource-control policy, không phải memory sandbox hay bảo vệ trước local Administrator.
- PLAN 24 không persist history hoặc deserialize polymorphic commands. Project history schema, recovery và untrusted durable data validation thuộc PLAN project tương ứng.

## Alpha channel boundary từ PLAN 25

- Alpha request chỉ chứa immutable `InternalImage`, operation enum, optional immutable replacement channel và integer threshold; không nhận path, stream, native pointer, UI object, executable hoặc DDS/archive metadata.
- Dimensions, pixel count và RGBA bytes được kiểm tra lại bằng `ImageImportResourcePolicy` với checked arithmetic. Threshold ngoài `0..255`, operation không xác định và replacement sai dimensions bị reject bằng structured failure.
- View/extract/replace/invert/threshold chạy bằng managed memory, không filesystem/process/temp/secret và không thêm native dependency. Service stateless nên concurrent calls không chia sẻ mutable state.
- Replace/invert/threshold giữ RGB byte-exact, kể cả hidden RGB khi alpha bằng 0; utility không premultiply nên không làm sai contract straight-alpha hoặc tự tạo fringe do zero hidden color.
- Cancellation được kiểm tra theo row và trước khi promote kết quả; failure/cancellation không trả partial image/channel. Full-resolution operation vẫn có peak-memory cost theo input/output; policy 100 triệu pixel/512 MiB giảm allocation abuse nhưng không phải memory sandbox.

## Game catalog boundary từ PLAN 26

- Production catalog là code-owned application metadata được validate trước khi đăng ký singleton; không load từ user settings, project JSON, filesystem hoặc remote endpoint.
- Stable `GameId` bị giới hạn vào machine-friendly lowercase ASCII grammar. `DisplayName` là presentation metadata có Unicode nhưng không được dùng làm identity, filesystem path hoặc security decision.
- `GameDefinition` của PLAN 26 không chứa executable path, archive filename/source path, tool/template hash, archive engine, region profile, raw country selection, install path hoặc secret. Product direction mới không bổ sung game-install metadata; export destination là machine-local state độc lập.
- Catalog reject null/empty/duplicate definition bằng structured issue và không phát hành partial catalog. Built-in validation failure làm bootstrap fail-fast thay vì xuất hiện muộn khi người dùng chọn game.
- Public collection và definition đều immutable; concurrent reads không chia sẻ mutable state. PLAN 26 không ghi filesystem, không chạy process, không gọi network và không tạo trust override từ local configuration.

## Mod definition boundary từ PLAN 27

- `ModId` dùng stable lowercase ASCII grammar và luôn đi cùng explicit `GameId`; display name không tham gia identity, path hoặc trust decision. Lookup cross-game là expected miss; duplicate `(GameId, ModId)` bị reject atomically.
- `ModRelativePath` canonicalize separator và reject rooted path, traversal, empty/dot segment, control character cùng ký tự filename bị cấm trên Windows. Đây là metadata validation; PLAN 27 không mở cover, template hoặc install destination trên filesystem.
- Archive mapping reuse `AuditionArchiveTemplate`; engine là typed explicit mapping, không suy từ extension. Catalog xác minh referenced game và region qua trusted `IGameCatalog`/`IGameRegionProfileResolver`, không nhận allowlist hoặc hash override từ user.
- `ModDefinition` chỉ chứa `RegionProfileId`; ACV country selection vẫn được resolve bên trong trusted region profile/archive execution boundary. Raw selector, executable path và process argument không được thêm vào Mod Catalog API.
- Production catalog là immutable application metadata và hiện rỗng có chủ ý vì roadmap chưa định danh một built-in Mod Type đầy đủ. Không load user JSON/settings/network để tự thêm hoặc override mod, engine, region, template hay legacy install mapping.
- PLAN 27 không chạy process, không đọc/ghi archive, không detect game install, không gọi network và không chứa secret. Legacy `InstallRelativePath` không được future workflow dùng để mutate game; template existence/hash verification, project snapshot, export và remote signed catalog thuộc boundary/PLAN tương ứng.

## Texture Manifest boundary từ PLAN 28

- Manifest là code-owned application metadata và gắn explicit với `(GameId, ModId)` đã tồn tại trong trusted Mod Catalog; không load hoặc override từ user settings, project JSON, filesystem hay network.
- Texture path reuse `ModRelativePath`: separator được normalize, còn rooted path, UNC, traversal, ADS, control character và Windows-forbidden segment bị reject. Duplicate/case-colliding paths bị reject atomically theo Windows filesystem semantics.
- Semantic `TextureSlotId`, category và recommended edit mode dùng bounded lowercase ASCII grammar. Display name, description và tags là presentation metadata có giới hạn; chúng không được dùng làm filesystem identity hoặc trust decision.
- Manifest không mở filesystem, parse DDS, chạy process hay chứa tool path/secret. DDS metadata thật vẫn phải đi qua `IDdsMetadataReader`; fallback chỉ trả raw filename/path và không đoán format, dimensions, category hoặc edit semantics.
- Production catalog rỗng có chủ ý cho đến khi có authoritative product metadata. PLAN 28 không tạo trust path cho external unsigned manifests và không triển khai smart scan, replacement hoặc archive execution.

## Smart Mod Scan boundary từ PLAN 29

- Request chỉ nhận typed Game/Mod identity và existing managed `IProjectArchiveWorkspace`; không nhận arbitrary root, executable path, archive argument hoặc trusted hash override. Game/Mod phải tồn tại trước khi scanner chạy.
- Recursive traversal, normalization, hashing, reparse rejection và Windows collision detection vẫn thuộc `IArchiveAssetScanner`; Smart Scan không có filesystem enumerator thứ hai. Mỗi DDS path được resolve lại trong extracted root qua `IPathSecurity` trước metadata/preview.
- Real DDS metadata luôn đến từ `IDdsMetadataReader`. Manifest không override dimensions/format/mips/header; raw assets không được đoán category, tags, edit mode hoặc semantic ID. Mapping chỉ dùng exact normalized path với `OrdinalIgnoreCase`.
- Thumbnail generation reuse controlled `IDdsPreviewService`, immutable encoded-memory import và bounded resize. Không có user-controlled process/tool path; full decoded image chỉ sống trong một iteration rồi được thay bằng thumbnail tối đa 256 mặc định/1024 hard-cap. PLAN 15 có thể tạo isolated temporary preview output và bắt buộc cleanup; extracted tree, working archive và manifest không bị sửa.
- Service stateless, xử lý texture tuần tự, propagate cancellation và chỉ publish complete immutable result. Fatal scanner/metadata/thumbnail/mapping failure không trả partial observed catalog. Unknown mapping và missing non-required slot là structured data, không phải exception.
- PLAN 29 không network, secret, persistent label write, UI, DDS replacement, archive repack hoặc template migration. `CanBeLabeled` chỉ biểu diễn capability cho admin workflow tương lai, không cấp quyền hoặc thay đổi trusted manifest.

## Template Versioning boundary từ PLAN 30

- Template trust identity gồm đủ typed `TemplateId`, opaque `TemplateVersion`, exact SHA-256 và `CompatibleGameBuild`. Version không thay thế integrity hash; filename, extension, display metadata hoặc lexicographic ordering không được dùng để suy ra trust/current version.
- Catalog là immutable code-owned metadata, validate atomically duplicate `(TemplateId, Version)`, conflicting hash và missing/multiple explicit current marker. Production catalog rỗng; user settings, project JSON, filesystem và network không được thêm hoặc override trusted entry.
- Project mới phải snapshot exact bốn thành phần identity. Resolver chỉ báo trung tính `CurrentVersionDiffers` vì version không có ordering; nó không suy ra upgrade/downgrade, mutate project, rebind sang current, ghi manifest hay tự chạy migration. Legacy snapshot thiếu version/build vẫn đọc được để diagnostics nhưng bị coi là invalid, không được default sang current.
- Compatible game build được so sánh exact ordinal. Hash mismatch, build mismatch, missing template và missing version là các trạng thái riêng; caller không được tiếp tục như exact match.
- PLAN 30 không chạy process, không download template/tool, không mở archive, không ghi pristine template, không gọi network và không chứa secret. Upgrade/downgrade/migration execution, UI consent và signed remote distribution thuộc PLAN sau khi được phê duyệt.

## Project Model `.audproj` boundary từ PLAN 31

- `.audproj` được xem là local data không tin cậy. Aggregate chỉ nhận typed identity, immutable collection, content hash và normalized relative reference; không chứa executable path, raw process argument, token, provider secret hoặc backend entitlement authority.
- `ProjectId` và `ProjectAssetId` là machine identity; project name chỉ là Unicode display metadata và tuyệt đối không tham gia workspace/file path. Texture/asset duplicate path được xét theo Windows `OrdinalIgnoreCase` và reject atomically.
- Template linkage là exact `TemplateIdentity` PLAN 30. Model không default version/hash/build, không rebind sang current catalog và không dùng filename làm template/version identity.
- Edit/history chỉ lưu revision cùng before/after asset references; dangling reference, duplicate revision và revision ngoài current snapshot bị reject. Không serialize `InternalImage` runtime object hoặc mutable edit-history session.
- PLAN 31 chưa có filesystem serializer, load/recovery, process, network hoặc workspace mutation. Những trust boundary đó chỉ được mở ở workflow PLAN tương ứng.

## Create Project Workflow boundary từ PLAN 32

- Request chỉ có typed Game/Mod identity và display name. Template source, premium policy, expected hash, engine và region đều đến từ trusted catalog/provider; local user input không thể override chúng.
- Entitlement luôn được kiểm tra trước acquisition. Provider production mặc định fail-closed cho đến khi trusted backend/template distribution được triển khai; desktop không chứa service-role/payment/provider secret và không tự coi local client là entitlement authority.
- Working archive/extracted tree chỉ được tạo qua managed workspace và existing verified-copy/archive engine. Interactive AuditionVN selection tiếp tục thuộc trusted region profile + redirected runner; workflow không xây process command hoặc chạm raw selector.
- `.audproj` và DDS metadata cache dùng ProjectId-derived filename dưới managed directories, reject reparse/path collision và atomic temp/flush/promote. Project name không tham gia path; cache không chứa thumbnail pixels, executable path hoặc secret.
- Failure/cancellation sau allocation xóa đúng project/cache ID và dispose isolated workspace. Không sửa pristine template; rollback failure là typed fatal result. PLAN 32 không tự recover/load, re-extract khi reopen, mutate texture hoặc reset project.

## Load/Recover Project boundary từ PLAN 33

- `.audproj` và metadata cache là local untrusted JSON: deserialize strict, giới hạn 64 MiB, reject unknown member/schema/ID/model/path collision. Project filename chỉ sinh từ exact GUID; display name không tham gia path.
- Recovery chỉ bind exact template version/hash/compatible build đã lưu. Thiếu version không fallback sang current; re-extract phải qua entitlement, trusted acquisition, region profile và existing archive service.
- Workspace reopen chỉ nhận lowercase 32-hex managed child, exclusive marker hợp lệ, directory layout đầy đủ và tree không reparse point. Manifest phải khớp project ID, workspace ID, template identity và normalized relative paths trước khi publish lease.
- Retention chỉ áp dụng sau atomic project save hoặc khi mở workspace project đã tồn tại. Workspace recovery mới bị hủy trước commit vẫn được xóa; retained workspace dispose chỉ nhả lock. Cache hỏng không là lý do mutate pristine archive.

## Texture State Machine boundary từ PLAN 34

- State machine không nhận absolute path, process/tool path hay user display metadata làm identity. Texture lookup reuse normalized `ModRelativePath` và Windows case-collision semantics của project model.
- `AiGenerated` chỉ xuất phát từ typed AI asset reference trong immutable project; không infer từ filename, display name, extension hay provider string. `Invalid`/`Missing` chỉ phản ánh explicit observation do orchestration tin cậy cung cấp.
- Evaluation là read-only, thread-safe, không network/process/filesystem, không log secret và không tự reset/replace texture. Filesystem mutation và pristine restore thuộc PLAN 35.

## Reset Texture / Reset Project boundary từ PLAN 35

- Reset chỉ nhận typed project/workspace + normalized relative texture path. Workspace descriptor phải khớp exact project/template/workspace identity; template acquisition không nhận arbitrary path từ UI/settings.
- Bản gốc texture chỉ đến từ extracted tree của regenerated workspace được tạo bằng exact trusted template. Source/global archive không mở ghi; target chỉ nằm trong current project workspace. Restore transaction dùng backup nội bộ, atomic replace và rollback khi scan/model/save fail.
- Full reset commit workspace mới trước khi xóa workspace cũ. Explicit removal chỉ chấp nhận active owned lease exact-reference; bỏ retention rồi xóa direct managed workspace, không nhận raw directory. Cleanup failure sau commit được report để retry, không xóa global template hay workspace khác.
- Workflow không tự tạo process arguments, không network/secret, không infer engine/region/version và không triển khai thumbnail cache.

## Thumbnail Cache boundary từ PLAN 36

- Thumbnail là derived local data, không phải nguồn sự thật cho DDS metadata hoặc content trust. Cache miss, stale key, malformed header, truncated/oversized payload hay enum không hợp lệ đều không được publish; entry hỏng được xóa best-effort và tái tạo từ DDS qua các service PLAN 15/20/21.
- Disk path chỉ nằm dưới managed `Cache/Thumbnails/v1`; filename là lowercase SHA-256 do service tự sinh. Caller không cung cấp cache path. Việc tạo/mở thư mục kiểm tra containment và reparse point qua `IPathSecurity`; source DDS vẫn được resolve bởi workspace/path-security boundary hiện có và chỉ mở read-only.
- Ghi cache dùng file tạm ngẫu nhiên trong cùng managed directory, flush rồi atomic move. Cancellation/failure dọn file tạm; lỗi ghi cache không biến thumbnail hợp lệ trong memory thành thất bại giả và không sửa source/archive/project.
- Giới hạn dimension, số entry memory/disk và tổng byte disk ngăn cache tăng không giới hạn. Single-flight chỉ khóa theo content key, có cancellation và không chạy network/process mới; lời gọi DirectXTex nếu cache miss vẫn đi qua `IDdsPreviewService` cùng executable allowlist/hash đã có.
- Cache không chứa secret, entitlement, executable path, raw command argument hay authoritative manifest metadata. PLAN 36 không cho local settings/user input override tool trust hoặc cache root.

## Lazy Loading boundary từ PLAN 37

- Metadata-first catalog không chứa decoded pixel buffer, absolute path, tool path hay secret. Asset path/hash đến từ observed scanner identity; request sai relative path hoặc SHA-256 bị reject trước dependency call.
- Thumbnail chỉ đi qua `IThumbnailCache`. Full texture chỉ đi qua explicit `LoadSelectedTextureAsync`, rồi reuse `IDdsPreviewService` hash-pinned và in-memory image importer; không có đường decode/process trực tiếp mới.
- Metadata do preview đọc lại phải exact-match snapshot từ Smart Scan trước khi pixels được publish. Mismatch được xem là stale selection và fail có cấu trúc, không silently dùng bytes đã thay đổi với metadata cũ.
- Service không cache full-resolution image, không mutate DDS/extracted tree/archive/project, không network và không nhận executable/config trust override. Cancellation/failure không trả partial image.

## Background Task Manager boundary từ PLAN 38

- Queue chỉ nhận typed job kind và internal delegate; snapshot/notification không chứa executable path, raw arguments, token, secret hoặc arbitrary result payload. Job implementation không được dùng manager để bỏ qua archive/DDS/image/AI trust boundary hiện có.
- Capacity, worker concurrency và completed-history đều bị giới hạn. Queue full/shutdown/cancel là structured result; một exception hoặc notification subscriber lỗi không được làm chết worker hay biến job khác thành success/failure giả.
- Progress stage/diagnostic code bị giới hạn chiều dài và grammar machine-readable trước khi publish; exception message không được đưa vào snapshot hoặc log. Log lỗi chỉ chứa task ID, typed kind và exception type.
- Application shutdown ngừng nhận job mới, complete channel và cancel token liên kết; queued/running job đi đến terminal cancellation. PLAN 38 chỉ giữ state in-memory, vì vậy không tuyên bố crash durability; stale session detection/cleanup thuộc PLAN 39.

## Temp Cleanup & Crash Recovery boundary từ PLAN 39

- Discovery/action chỉ áp dụng direct child có ID lowercase-hex 32 ký tự dưới managed `Temp/Workspaces`. Raw path, project name, archive filename, PID hoặc timestamp không bao giờ được dùng làm delete target.
- Exclusive lock là authority để phân biệt active/stale; PID/session/timestamp chỉ là diagnostic metadata nên PID reuse hoặc clock skew không thể cấp quyền cleanup. Startup chỉ detect/offer, tuyệt đối không auto-delete retained hay incomplete workspace.
- Mỗi recovery/cleanup revalidate containment, marker version/grammar, full tree reparse points và lock ngay tại thời điểm action. Lock race hoặc access ambiguity trả `ActiveOrInaccessible`; invalid/missing marker trả `Unsafe` và giữ nguyên dữ liệu.
- Explicit cleanup giữ exclusive delete-sharing handle trong lúc xóa đúng workspace root. Nó không traverse ra ngoài, không nhận arbitrary path và không chạm Projects, SecureTemplateCache, fixture, pristine archive hoặc workspace active khác.
- Marker không chứa secret; process ID/session ID không phải authentication token. Recovery vẫn phải đi qua project/template validation PLAN 33 khi orchestration mở project; workspace recovery riêng không nâng trust cho `.audproj` hay archive bytes.

## Design System boundary từ PLAN 40

- Tất cả XAML/resource là code-owned và compile cùng ứng dụng; không load remote font/image/dictionary, user-controlled URI, WebView content hoặc dynamic XAML.
- Component dùng semantic `ThemeResource`; raw colors chỉ tồn tại trong primitive layer. Default/Light/HighContrast có cùng key contract, focus indicator luôn explicit và trạng thái không được chỉ dựa vào màu.
- Shared acrylic/gradient/shadow được giới hạn ở component resource; motion chỉ dùng opacity/transform ngắn để giảm overdraw/layout churn. Clarity và system accessibility behavior thắng decoration.
- Design token/style không chứa secret, filesystem path, executable path, entitlement hay business/security decision. PLAN 40 không thêm UI workflow hoặc App Shell.

## App Shell boundary từ PLAN 41

- Route và label là code-owned presentation metadata; không load dynamic XAML, remote resource hoặc user-controlled navigation identity.
- Top bar không coi credits, connection, notification hoặc account text là authority. Trạng thái thương mại về sau vẫn phải đến từ trusted backend và client chỉ hiển thị.
- Shell/ViewModel không nhận executable path, raw archive argument, country selector, trusted template hash hoặc secret; không gọi filesystem/process/network.
- Navigation không làm thay đổi project/workspace lifecycle. Đóng cửa sổ vẫn đi qua graceful host shutdown hiện có và không xóa retained project workspace.

## Home creation boundary từ PLAN 42

- Normal-user flow chỉ nhận typed Game/Mod selection và project display name. Không có filesystem archive selection, executable path, raw ACV argument, country selector, template hash override hoặc local entitlement override.
- Compatible Mod Type đến duy nhất từ code-owned `IModCatalog`; khi catalog production rỗng, UI fail-closed bằng empty state và disable Create.
- Long-running create đi qua Background Task Manager rồi existing Project Creation Service. UI không tự extract, scan, acquire template, save `.audproj` hoặc clear recovery flag.
- Raw exception, process output và internal diagnostic code không hiển thị cho người dùng. Presentation message chỉ mô tả recovery action an toàn; diagnostics tiếp tục thuộc structured logs hiện có.
- Active project session chỉ giữ domain aggregate cùng managed retained-workspace lease; project name không tham gia path và session dispose không xóa retained project data.

## Project Workspace UI boundary từ PLAN 43

- UI chỉ nhận normalized relative texture identity và display metadata từ Smart Scan. Absolute workspace path, source SHA-256, executable path, process argument, template hash và region selector không được publish trong presentation model.
- Folder/search/filter không enumerate filesystem; chúng chỉ lọc immutable scan result. Metadata/format/dimensions đến từ existing DDS metadata boundary và state đến từ typed state machine.
- Workspace activation dùng PLAN 38 để scan, hỗ trợ cancel và safe presentation error. UI không log/hiển thị raw exception, scanner diagnostic hoặc failed absolute path.
- Preview không decode eager và edit buttons chưa có authority/workflow đều disabled. PLAN 43 không ghi DDS, project, manifest, cache hoặc pristine template.

## Texture Grid boundary từ PLAN 44

- Search và facets chỉ lọc immutable scan snapshot; chuỗi tìm kiếm bị giới hạn 256 ký tự và không được dùng làm path, query mạng, command argument hoặc cache key tùy ý.
- Presentation model không công bố absolute path, source SHA-256, executable path hay diagnostic nội bộ. Source asset đầy đủ chỉ tồn tại trong mapping private để gọi đúng lazy-loading boundary.
- Grid không eager-decode full texture. Chỉ container đang hiện thực hóa mới enqueue typed thumbnail job với kích thước code-owned 192 px; container tái sử dụng phải xác minh lại item identity trước khi publish bitmap.
- Thumbnail failure/cancellation không làm phát sinh partial image hay mutation. Mọi decode/cache miss tiếp tục đi qua trust, containment, resource limit và hash-pinned DirectXTex policy của PLAN 15/36/37.

## Crop/Resize Canvas boundary từ PLAN 45

- Editor source chỉ đến từ selected `SmartTextureAsset` private mapping và managed project workspace. UI không nhận absolute path, source hash, executable path, process argument hoặc trust override.
- Full texture chỉ được decode theo explicit route activation qua typed `Convert` background job và `LoadSelectedTextureAsync`; metadata được lazy service revalidate trước khi pixels được publish. Cancel đi qua exact task ID, không kill process tùy ý.
- Zoom/pan/crop/mode là numeric hoặc enum typed. NaN/Infinity/out-of-range crop và transform bị PLAN 22 reject; UI không dùng geometry làm path, command, cache identity hoặc log payload.
- Full `InternalImage` và WinUI bitmap chỉ sống trong editor route rồi được release khi rời route. Row-bounded channel conversion giảm peak managed allocation; resource limits PLAN 15/20 vẫn áp dụng cho decode.
- PLAN 45 không execute resize, encode, replace, save, pack hoặc mutate pristine/project data. Preview state không cấp authority cho Apply; validate/atomic replacement thuộc PLAN 47.

## Project Validator boundary từ PLAN 48

- Validation chỉ nhận exact `AuditionProject` và retained `IProjectArchiveWorkspace`; mismatch project ID,
  workspace ID hoặc normalized workspace paths fail-closed. Không nhận absolute path hay tool path từ UI.
- Enumeration đi qua `IArchiveAssetScanner`, vì vậy containment, reparse-point rejection, duplicate identity
  và read failure tiếp tục áp dụng. DDS header hiện tại được đọc qua `IDdsMetadataReader`; metadata baseline
  chỉ dùng để đối chiếu dimensions/format và không cấp trust cho file đã thay đổi.
- ACV Tool 5 probe dùng production allowlist/hash policy của PLAN 07 với absolute app-local path. Missing,
  hash mismatch, filename/manifest invalid hoặc trust location không khả dụng đều trở thành structured error;
  validator không launch process.
- Kết quả không chứa absolute path, expected/actual hash, raw exception hoặc tool output. Cancellation không
  tạo partial mutation; toàn bộ operation không ghi project/cache/DDS/archive/pristine template.

## Build Pipeline boundary từ PLAN 49

- Build chỉ nhận exact immutable project + retained workspace; không nhận output path, executable path,
  extension, raw arguments hoặc country selection từ UI. Save và PLAN 48 validation là precondition trước
  khi cấp temporary build workspace.
- Project working archive được mở read-only, hash lại theo workspace descriptor rồi copy durable. Extracted
  tree được traverse không theo reparse point, resolve containment từng entry và giới hạn file count/total
  bytes trước khi copy. Temporary workspace ngẫu nhiên bị cleanup trên mọi terminal path.
- Pack chỉ gọi `IAuditionArchiveService`; production ACV engine provision/hash-check `acv.exe`, dùng absolute
  path, structured arguments, redirected stdin/stdout/stderr và trusted region profile. Build service không
  launch process hoặc parse raw ACV output.
- Candidate phải tồn tại, non-empty, đọc được và được SHA-256 trước promotion. Copy sang project Output dùng
  temp cùng filesystem, durable flush, hash verification và atomic replace/move. Existing output có backup
  transaction; final `.audproj` save fail/cancel/exception phục hồi bytes cũ hoặc xóa output mới.
- Log/result không chứa absolute path, asset filename list, raw stdout/stderr hoặc trusted expected hash.
  SHA-256 là integrity/content identity, không phải chữ ký hay DRM.

## Product Gate C boundary từ PLAN 50

- Gate chỉ mutate disposable working copy. Test hash lại pristine `015.ab`, companion keydat, approved
  `acv.exe` và source target sau pack/re-extract để phát hiện mutation ngoài ý muốn.
- Production pack/re-extract tiếp tục đi qua archive abstraction, tool allowlist/hash, absolute executable,
  isolated workspace, structured arguments và resource/path/reparse protections hiện có.
- Controlled artifact có SHA-256 xác định nhưng hash không phải signature, provenance hoặc compatibility
  proof. Artifact/runtime outputs nằm trong ignored output root; Git artifact scan phải chứng minh không có
  proprietary fixture, generated archive, keydat hay tool binary mới được track.
- Manual game observation nằm ngoài application trust/acceptance boundary. Không có code launch, login,
  automation hoặc runtime observation được thêm để làm Gate C PASS, và không tuyên bố compatibility in-game
  khi chưa có external QA riêng.

## Export Destination boundary từ PLAN 51

- Destination path/filename là untrusted machine-local input. Validator canonicalize absolute directory,
  reject missing/inaccessible/unavailable/reparse path, validate one safe filename segment và bind extension
  với trusted archive filename contract. Destination không cấp product/trust identity.
- Existing file luôn là collision trừ khi caller cung cấp explicit `ReplaceExisting`; validation không mutate
  existing bytes và không tạo probe/temp file. Access denied không bị làm mờ thành not-found.
- PLAN 51 chỉ xác nhận intent tại một thời điểm nên vẫn có TOCTOU. PLAN 52 phải revalidate, mở/write an toàn,
  hash candidate và atomic promote trong transaction giữ destination cũ tới commit.

## Atomic Archive Export boundary từ PLAN 52

- Source chỉ là exact PLAN 49 build output trong matching ready project workspace; status/path/stored hash được
  kiểm tra lại trước copy. Export không nhận arbitrary source file và không chạy archive tool.
- Candidate/backup names là random code-owned safe segments cùng destination root. Copy dùng create-new,
  async/write-through/flush; size và SHA-256 phải match trước và sau atomic promotion.
- Explicit overwrite dùng backup transaction. Failure trước promote không chạm final; failure sau promote rollback
  destination cũ. Rollback failure không bị nuốt và recovery backup được giữ để operator xử lý.
- Service không log full source/destination path hoặc hashes, không persistence machine path và không chạm game.

## Build & Export UI boundary từ PLAN 53

- Folder picker chỉ là presentation boundary; path trả về vẫn là untrusted machine-local input và phải qua PLAN 51.
  ViewModel snapshot directory/filename/overwrite intent trước khi enqueue để UI mutation không đổi transaction.
- Destination preflight chạy trong Background Task Manager trước build; PLAN 52 vẫn revalidate ngay trước mutation,
  vì preflight không loại bỏ TOCTOU. Existing destination chỉ được replace khi checkbox explicit đã được snapshot.
- ViewModel chỉ compose exact active project/workspace với PLAN 49/52 services; không nhận arbitrary source archive,
  không gọi process/copy API, không suy ra game/template/region/engine từ destination và không persist path vào
  `.audproj` hoặc settings.
- Presentation không hiển thị raw exception, archive-tool output hay internal diagnostic code. Final full path và
  SHA-256 chỉ xuất hiện sau success theo yêu cầu user-visible deliverable; chúng không được log và hash không phải
  chữ ký/provenance proof.
- Cancellation chỉ truyền qua exact Background Task ID và service token. UI không kill process tùy ý, không tạo
  game filesystem authority và không thêm install/restore/launch/login/runtime validation.
- Cập nhật build state dùng compare-and-swap dưới session gate với exact project/workspace reference; job cũ không
  thể ghi đè project mới vừa được activate. Folder-picker exception chỉ log exception type và trả safe UI message.

## Batch Build & Export boundary từ PLAN 54

- Mỗi job chỉ dùng exact project/workspace object; batch không nhận project file path, source archive path, tool path
  hoặc raw command. Workspace lease vẫn thuộc caller và phải còn valid tới terminal result.
- Filename do service sinh từ code-owned prefix + ProjectId + trusted extension; project name không thành path.
  Tất cả destination vẫn là untrusted input và qua PLAN 51 trước build, PLAN 52 revalidation trước mutation.
- Canonical path comparison dùng ordinal-ignore-case. Duplicate path mặc định fail trước build; explicit serialize
  còn yêu cầu từng job `ReplaceExisting`, và per-path semaphore ngăn concurrent promotion tới cùng final file.
- Code-owned job/concurrency limits cùng PLAN 38 queue ngăn resource exhaustion và unbounded ACV process. Batch token
  được đăng ký về exact Background Task ID; queued/running job đi đến typed terminal cancellation.
- Một job không cấp authority cho job khác và không chia sẻ writable temp workspace/keydat. PLAN 49/52 tiếp tục sở
  hữu isolated workspace, hash verification, atomicity và rollback; batch layer không copy file hoặc gọi process.
- Progress/diagnostic chỉ dùng stable code, job index/ID và số đếm; không log destination, hash, archive asset list,
  raw exception hoặc tool output. Batch không discover/mutate/install/launch/automate game.

## File-Only Production Gate boundary từ PLAN 55

- Pristine `015.ab`, `015.keydat`, `acv.exe`, extracted fixture và target DDS chỉ được đọc/hash; mọi template pack,
  edit, build và verify diễn ra trong randomized managed workspace. Gate hash lại pristine inputs sau workflow.
- Template chuẩn bị từ fixture được pack bằng production archive service rồi copy vào trusted test source; Create
  Project vẫn bắt buộc source-hash verification trước working copy/extract/scan.
- Artifact user-named được kiểm tra size/hash sau atomic export. Re-extract chỉ dùng bản stage byte-identical trong
  verify workspace với canonical ACV basename; stage hash phải trùng artifact trước khi tool được chạy.
- Re-extracted inventory được so theo normalized relative path và SHA-256: target duy nhất phải đổi, mọi non-target
  phải byte-identical. Thiếu file, file thừa, corruption, metadata mismatch hoặc pristine mutation đều fail gate.
- Fix validator cho separator chỉ chuẩn hóa logical relative identity; nó không nới path containment, reparse-point,
  archive hash, workspace ID/project ID hoặc tool integrity checks.
- Generated archive nằm dưới ignored controlled output; fixture/tool/keydat/DDS/generated archive không được Git
  track. SHA-256 là integrity identity, không phải chữ ký hoặc bằng chứng tương thích runtime.

## Batch Build Summary boundary từ PLAN 56

- Texture outcomes là input không tin cậy: relative path phải qua `ModRelativePath`, identity duplicate bị reject,
  status phải là enum xác định và diagnostic code chỉ nhận bounded uppercase ASCII stable-code characters.
- Reported `Changed` set phải khớp exact immutable `AuditionProject.EditedTextures`; failed/skipped outcome không
  cấp quyền sửa file và không thể che một edited texture khỏi summary.
- Orchestrator không nhận absolute path/tool path, không gọi filesystem/process/network trực tiếp và không tự pack.
  Mọi mutation, isolation, tool integrity, atomic promotion và rollback vẫn nằm trong PLAN 49.
- No-change/mismatch/input invalid fail trước build. Build failure/cancellation giữ typed terminal result và summary
  quan sát được, không publish partial success hoặc gọi lại build ngầm.

## AI Provider Abstraction boundary từ PLAN 57

- `IAiService` không có tham số provider API key, privileged token, endpoint override hoặc raw authorization header;
  desktop không thể cấp embedded provider secret qua contract này.
- Prompt là bounded value object, target dimensions có hard limit và image/mask reuse immutable `InternalImage`.
  Contract không log hoặc persist prompt/pixels; implementation PLAN 57 không giữ reference sau khi trả kết quả.
- Desktop bind fail-closed implementation không chạy network. Khi trusted backend chưa tồn tại, mọi operation trả
  structured unavailable; pre-cancellation trả cancellation và không publish image.
- `AiImageResult` success mới chứa image; failure/cancelled không mang partial pixels. Progress chỉ chứa phase,
  percentage và stable diagnostic code, không chứa prompt, image, credential hoặc provider response.
- PLAN 57 chưa tạo auth, gateway, credit hoặc provider transport. Các trust checks server-side không được mô phỏng
  trong client và chỉ được triển khai ở PLAN tương ứng.

## Supabase Auth boundary từ PLAN 58

- Desktop chỉ được cấu hình Supabase project HTTPS URL không có user-info và publishable key bounded. Không có
  service-role key, AI provider credential, payment secret, master encryption key hoặc private signing key trong
  contract, source, settings hay request API.
- Password và session secret có string representation redacted; auth transport không log request/response/token.
  Email/password/token/config đều bounded và reject control characters phù hợp. Remote reject, network failure,
  protocol error, secure-store failure và cancellation trả diagnostic code typed, không trả raw provider body.
- Production HTTP client không tự follow redirect, nên credential request không được chuyển tiếp sang origin khác.
- Session secret chỉ qua `ISecureSessionStore`; production store dùng Windows Credential Manager Generic Credential,
  không plain JSON. Credential payload có hard size limit, malformed content bị reject, managed serialization buffer
  được zero, và local session chỉ bị xóa sau khi remote sign-out thành công.
- Refresh token rotation được bảo vệ bằng semaphore: mỗi refresh load token trong critical section và persist cặp
  token mới trước khi request kế tiếp chạy. Lỗi persist không được báo success. Stored token malformed không được
  đưa vào HTTP authorization header.
- Access token vẫn là bearer credential và Windows user context vẫn là trust boundary cục bộ; Credential Manager
  không biến client thành trusted authority. PLAN 59 phải validate access token/schema tại backend và giữ toàn bộ
  provider credentials/server authority ngoài desktop.

## Backend Trusted Gateway boundary từ PLAN 59

- Gateway là process/server deployment boundary riêng. Fallback authorization bảo vệ mọi endpoint mới; health là
  anonymous exception duy nhất. Thiếu/sai bearer token trả 401, Supabase validation outage/config thiếu trả 503 và
  không chạy business handler. Token/header/body không được log bởi code gateway.
- Token không được decode hoặc tin cậy cục bộ từ payload. Gateway gửi token tới exact Supabase Auth `/auth/v1/user`;
  Supabase xác minh signature, expiration và session trước khi trả user. Outbound auth client không follow redirect,
  có timeout/cancellation, bounded response và không gửi service-role key. User identity đến duy nhất từ returned
  UUID; client không gửi UserId cho endpoint.
- JSON unknown member bị cấm. AI body tối đa 12 MiB, mỗi decoded image/mask tối đa 4 MiB, entitlement body tối đa
  16 KiB; prompt/dimensions/template identity và operation-specific presence/absence được validate. Signature check
  chỉ là schema preflight, không thay thế full decoder/content safety trong provider implementation tương lai.
- Client không có request để set balance, cost, refund, successful payment hoặc entitlement result. Credit route là
  GET-only. PLAN 59 không tạo ledger mutation; PLAN 60 phải giữ transaction/idempotency/server authority.
- Provider endpoint/API key chỉ nằm trong server configuration object có redacted string representation. Default
  AI/credit/entitlement services fail closed. Trusted service diagnostic/output vẫn được grammar/length validate
  trước response để raw provider error, signed URL hoặc secret không bị phản chiếu tới client.
- Gateway chưa cấu hình TLS termination, rate limiting, provider adapter, database/RLS, audit persistence hoặc
  distributed abuse controls; các concern này phải được hoàn tất ở deployment/security PLAN tương ứng. ASP.NET
  production deployment phải terminate TLS và cấp secrets qua secret store/environment, không appsettings Git.

## Credit authority boundary từ PLAN 60

- Credit tables nằm trong private PostgreSQL schema, bật RLS và revoke toàn bộ table/function privilege khỏi
  `PUBLIC`, `anon`, `authenticated`. `service_role` chỉ được SELECT wallet projection và EXECUTE năm exact mutation
  functions; không được direct INSERT/UPDATE/DELETE tables. Connection string là server secret và Gateway chỉ chấp
  nhận TLS `Require`, `VerifyCA` hoặc `VerifyFull`; option rendering luôn redacted.
- Ledger, refund và idempotency rows bị trigger chặn UPDATE/DELETE. Wallet/reservation là mutable projections nhưng
  chỉ `SECURITY DEFINER` functions có fixed safe search path được thay đổi. Functions kiểm tra nonnegative balance,
  positive amount, overflow, valid state transition, reservation/capture ownership và refund aggregate cap.
- Mỗi mutation chạy trong explicit database transaction. Advisory lock serialize concurrent replay của cùng
  `(user, operation, idempotency key)`; wallet/reservation/captured-ledger row locks chống double-spend,
  capture-vs-release race và concurrent over-refund. Request SHA-256 canonical được lưu cùng unique idempotency key;
  replay cùng payload trả stored result, payload khác trả `CREDIT_IDEMPOTENCY_CONFLICT`.
- User identity tiếp tục đến từ verified Supabase subject của PLAN 59. Public API chỉ có `GET /v1/credits`; không có
  request contract/route để client set balance, cost, refund, grant hoặc successful payment. Capture cost và refund
  amount nằm trong internal server contract cho pricing/job/payment authorities tương lai, không phải client authority.
- Database/Npgsql errors được collapse thành stable unavailable/rejected code; raw SQL error, connection string và
  credential không được trả/log bởi ledger code. Cancellation được truyền tới open/read/commit và rollback xảy ra
  khi transaction chưa commit.
- Chưa có live PostgreSQL fixture trong repository, payment webhook, pricing rules, ledger retention/archive policy
  hoặc operational reconciliation dashboard. SQL migration/contract và service gates được kiểm thử offline; deployer
  phải apply migration bằng trusted migration role trước khi cấu hình Gateway. Pricing thuộc PLAN 61.

## AI pricing authority boundary từ PLAN 61

- Giá AI chỉ được nạp từ server configuration vào immutable process catalog. Version chỉ cho phép grammar bounded;
  effective time phải là UTC round-trip timestamp; mọi operation phải có integer credit cost trong giới hạn dương.
  Catalog thiếu/sai/chưa hiệu lực dùng `UnavailableAiPricingService` hoặc trả unavailable, không fallback sang client.
- Quote endpoint yêu cầu verified Supabase subject, giới hạn body 4 KiB, cấm unknown JSON member và enum số. Contract
  client không có cost, price, discount, provider cost, balance, refund hoặc payment state. Expected version chỉ là
  optimistic display token, không cho phép client chọn catalog hay số credit.
- Stale version trả conflict cùng current safe quote để client refresh estimate. Quote là read-only: không phụ thuộc,
  gọi hoặc mutate `ICreditLedgerService`; vì vậy invalid/unavailable pricing không thể reserve/charge một phần.
- PLAN 62 phải resolve giá hiện hành ở server tại transactional job enqueue/capture boundary. Không được dùng amount
  từ request hoặc quote cache phía client làm final charge. PLAN 61 không chứa provider call, job state, payment flow,
  database schema mới hay secret mới; live pricing config/reload/audit là trách nhiệm deployment vận hành.

## AI job authority boundary từ PLAN 62

- Enqueue/cancel/history đều cần authenticated Gateway principal; request không có UserId, price, balance, provider/model
  credential hoặc final charge. Job metadata giới hạn 16 KiB tại DB và typed input reference/dimensions/byte count tại
  Gateway; không persist raw image, token hay secret trong job row/log.
- Enqueue và credit reserve là một SQL function transaction, có advisory lock cùng unique owner/idempotency key.
  Payload conflict bị reject; replay trả row hiện có nên không double-reserve. Capture/release dùng deterministic internal
  keys và PLAN 60 row/advisory locking, không thực hiện wallet arithmetic trong C#.
- Lease token là worker-only capability. Claim dùng row lock + `SKIP LOCKED`; stale worker không thể complete nếu lease
  hết hạn/sai token. Retryable failure chỉ requeue; terminal failure/cancel/lease exhaustion release open reservation.
  Complete validate final cost không vượt reserve, output reference và provider request ID trước capture.
- RLS được bật và mọi quyền trực tiếp bị revoke khỏi `PUBLIC/anon/authenticated`. `service_role` chỉ đọc job history và
  execute năm exact job functions; không được direct INSERT/UPDATE/DELETE. API history luôn filter verified owner và
  không trả lease/provider request ID.
- Chưa chạy migration trên live/staging PostgreSQL, nên chưa tuyên bố concurrency/rollback thực tế. Contract SQL và
  integration shape được kiểm thử offline; PLAN 85 vẫn là real PostgreSQL concurrency gate. Rate limiting và provider
  worker vận hành vẫn là technical debt/deployment concern.

## AI Studio client boundary từ PLAN 63

- Page/ViewModel không nhận hoặc log bearer/refresh token. Cloud adapter lấy session từ `ISecureSessionStore`, yêu cầu
  refresh qua `IAuthenticationService` khi gần hết hạn và chỉ đặt token trong Authorization header tới exact HTTPS
  Gateway root. Redirect bị tắt và timeout 15 giây từ composition root; credentials không nằm trong URL/body/settings.
- Gateway response được đọc streaming với hard cap 256 KiB; operation/state/UUID/credit/version/timestamp đều parse typed
  và invalid payload fail closed. Provider request ID, lease token, database data và raw server exception không được hiển thị.
- Prompt/negative prompt tối đa 4.000 ký tự; model/quality chỉ nhận closed UI choices và không mang charge authority.
  AI work qua cancellable background manager. Offline/auth/storage/network failure chỉ vô hiệu commercial feature;
  project/local build/export không bị mutate hay block.
- Completed output chỉ trở thành immutable `InternalImage` preview trong memory. Không có code path từ AI Studio tới
  `ITextureApplyService`, DDS conversion, project save, archive pack/export hoặc game filesystem. Apply thuộc PLAN 65.

## AI mask boundary từ PLAN 64

- Mask là dữ liệu local không có credential, provider error, token hay charge authority. Kích thước phải khớp nguồn,
  tổng pixel bị giới hạn và mọi brush setting/point đều được validate trước khi tạo bản mask mới.
- Stroke, clear, invert, undo/redo và preview composition chỉ thay state trong memory. Missing/invalid source, history vượt
  giới hạn hoặc cancellation không mutate project texture, DDS, archive hay pristine template.
- Persistence dùng `ISecureWorkspace.ResolveRelativePath` với relative path cố định, không nhận path từ prompt/client.
  Ghi file tạm ngẫu nhiên trong cùng thư mục, flush và replace atomically; cleanup log chỉ loại exception, không log byte mask.
- Không serialize mask theo từng stroke và không tự gửi mask ra network. Việc dùng mask trong request, provider execution,
  credit lifecycle và explicit Apply thuộc PLAN 65; PLAN 64 không tạo đường vòng tới game filesystem.

## AI execution boundary từ PLAN 65

- Provider credential, endpoint và trusted model profile chỉ tồn tại server-side. Desktop gửi semantic operation,
  allowlisted public option, bounded prompt/geometry và opaque content ID; final credit cost vẫn do PLAN 61 resolve tại
  PLAN 62 enqueue, không có client cost/balance/refund/provider-success authority.
- Upload/download đều authenticated, body bounded 16 MiB, content type allowlist và owner/kind enforcement. Input/provider
  output là untrusted: server media validator phải xác nhận bytes/dimensions/pixels, exact mask-source alignment và SHA-256
  trước execution/download. Raw local path, public provider URL và storage service credential không thuộc contract.
- Job claim dùng existing `FOR UPDATE SKIP LOCKED` lease. Validated output phải persist private trước atomic complete/capture.
  Known pre-billable failure/cancel dùng existing fail/cancel release path. Outcome không chắc chắn chuyển migration mới
  sang `ReconciliationRequired`, xóa lease nhưng giữ reservation; không tự retry/capture/release.
- Default Gateway thiếu content/provider/media implementation trả unavailable và không charge/success giả. External
  provider network integration và live PostgreSQL migration/concurrency vẫn NOT VERIFIED; reconciliation tooling là
  technical debt cần một PLAN/deployment decision riêng.
- Desktop output dùng bounded streaming và existing `IImageImportService`, chỉ publish immutable `InternalImage` preview.
  Explicit Approve mới gọi existing atomic Apply/Match Original/DDS validation/history. Backend failure không ảnh hưởng
  local project editor, DDS, build hoặc export; không có code game install/launch/runtime.
# Prompt preset boundary từ PLAN 66

- Prompt/preset là untrusted bounded user text, không executable và không được chứa hay điều khiển provider credential/endpoint, trusted model, price/credit, entitlement, user identity hoặc private content reference.
- Local JSON schema-v1 cấm unknown fields/numeric enums, có 2 MiB/1.000-entry limits, ghi atomic và cô lập malformed/unsupported schema; không silent migration. Same ID+version khác nội dung bị isolate thay vì chọn ngầm.
- Cloud catalog chỉ được nhận qua `ICloudPromptPresetService.ListOwnedAsync`; production adapter chưa cấu hình nên fail closed. Việc chọn preset chỉ sửa UI text, không submit job, reserve/capture credit, mutate mask/image/project/archive hoặc tự Apply/build.

## Secure local session storage audit từ PLAN 69

- `WindowsCredentialSessionStore` vẫn là production store duy nhất và vẫn dùng Windows Credential Manager Generic Credential. Backend Win32 nội bộ chỉ là test seam, không phải persistence implementation thứ hai; public constructor/`ISecureSessionStore` không nhận file/path/config.
- Credential payload schema-v1 cấm unknown field và future schema. Legacy PLAN 58 payload không có schema được decode strict rồi rewrite vào cùng credential target trước khi Load trả success; migration write failure fail closed. Không có plaintext migration file.
- Một process-wide semaphore serialize read/write/delete của target duy nhất qua mọi store instance. Rotation overwrite credential, delete not-found idempotent, token/blob bounded; managed serialization/read buffers và unmanaged write buffer được zero.
- Release scan PLAN 68 kiểm tra binary/PDB/log/crash. Token không được persist trong settings/project/preset/log. Credential Manager bảo vệ theo Windows user context nhưng không chống Administrator/fully compromised account hoặc process-memory dump.

## Server-authoritative entitlement boundary từ PLAN 70

- Entitlement record service tại Gateway là authority. Client không gửi/chọn user, expiry, nonce, audience, decision, cost hoặc `IsPremium`; verified Supabase principal là identity duy nhất.
- Grant schema-v1 ký ES256/P-256, sống đúng năm phút và bind user + closed scope + code-owned audience + exact template/game/mod khi applicable. Signature, owner, scope, audience, issued/expiry và resource grammar đều được validate trước atomic one-time nonce consume.
- Private signing PEM chỉ thuộc deployed server secret store; không có private key trong desktop/repo/log. Grant không được persist vào settings/project/preset. Invalid/tampered/expired/cross-user/wrong-audience/replay đều fail closed.
- Default record/signing/replay services unavailable; vì vậy premium AI không hoạt động offline hoặc khi backend/config/nonce store lỗi. Nonce store production phải durable/distributed; in-memory fake chỉ dùng test. Template catalog/download/package/cache thuộc PLAN 71 và chưa được triển khai.
