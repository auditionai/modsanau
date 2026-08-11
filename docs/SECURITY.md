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
- `GameDefinition` của PLAN 26 không chứa executable path, archive filename/source path, tool/template hash, archive engine, region profile, raw country selection, install path hoặc secret. Các trusted mapping đó chỉ được thêm ở PLAN tương ứng với validation riêng.
- Catalog reject null/empty/duplicate definition bằng structured issue và không phát hành partial catalog. Built-in validation failure làm bootstrap fail-fast thay vì xuất hiện muộn khi người dùng chọn game.
- Public collection và definition đều immutable; concurrent reads không chia sẻ mutable state. PLAN 26 không ghi filesystem, không chạy process, không gọi network và không tạo trust override từ local configuration.

## Mod definition boundary từ PLAN 27

- `ModId` dùng stable lowercase ASCII grammar và luôn đi cùng explicit `GameId`; display name không tham gia identity, path hoặc trust decision. Lookup cross-game là expected miss; duplicate `(GameId, ModId)` bị reject atomically.
- `ModRelativePath` canonicalize separator và reject rooted path, traversal, empty/dot segment, control character cùng ký tự filename bị cấm trên Windows. Đây là metadata validation; PLAN 27 không mở cover, template hoặc install destination trên filesystem.
- Archive mapping reuse `AuditionArchiveTemplate`; engine là typed explicit mapping, không suy từ extension. Catalog xác minh referenced game và region qua trusted `IGameCatalog`/`IGameRegionProfileResolver`, không nhận allowlist hoặc hash override từ user.
- `ModDefinition` chỉ chứa `RegionProfileId`; ACV country selection vẫn được resolve bên trong trusted region profile/archive execution boundary. Raw selector, executable path và process argument không được thêm vào Mod Catalog API.
- Production catalog là immutable application metadata và hiện rỗng có chủ ý vì roadmap chưa định danh một built-in Mod Type đầy đủ. Không load user JSON/settings/network để tự thêm hoặc override mod, engine, region, template hay install mapping.
- PLAN 27 không chạy process, không đọc/ghi archive, không detect game install, không gọi network và không chứa secret. Template existence/hash verification, project snapshot, install và remote signed catalog vẫn thuộc boundary/PLAN tương ứng.

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
