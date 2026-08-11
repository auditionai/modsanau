# Kiến trúc Audition AI Mod Studio

## Recursive Asset Scanner (PLAN 11)

Luồng production là `Working Archive → Extract → extracted directory của IProjectArchiveWorkspace → Recursive Scan → ArchiveAssetCatalog`. Scanner không đọc cây `015\` ở repository và không chứa logic DDS decode/convert, UI hay pack archive.

Contract/model (`ArchiveAsset`, `TextureAsset`, `ArchiveAssetCatalog`, `IArchiveAssetScanner`) nằm trong Core. Implementation filesystem `ArchiveAssetScanner` nằm trong Projects và chỉ nhận project workspace đã được cấp. DDS được phân loại thành `TextureAsset`; PNG, SLK, RGM và extension khác vẫn là asset catalog hợp lệ.

Identity ổn định là normalized relative directory path cộng exact filename, được biểu diễn bởi `RelativePath`; không dùng filename đơn lẻ và không đổi case/name. Kết quả sort bằng `StringComparer.Ordinal`. Duplicate logic theo filesystem Windows được phát hiện bằng `StringComparer.OrdinalIgnoreCase` và không bị overwrite.

Scanner duyệt không theo reparse point, resolve lại từng relative path qua `IPathSecurity`, hash SHA-256 theo stream 64 KiB, hỗ trợ cancellation/progress và không giữ mutable global state. Policy lỗi là fail toàn scan để không phát hành catalog thiếu mà caller tưởng là đầy đủ.

## Real archive repack round-trip (PLAN 12)

Luồng đã kiểm chứng thật là `project working archive → extracted tree không sửa → PackAsync → mutate working archive → copy repacked archive sang disposable trusted source → extract lại → asset catalog comparison`. PLAN 12 chưa promote sang `BuildOutput`; working archive và final promoted output vẫn là hai lifecycle cần tách ở PLAN sau.

ACV Tool 5 chạy pack bằng structured arguments tương đương `acv -ca 015.ab ..\Extracted\015` trong isolated `WorkingDirectory`; đường dẫn tương đối này trỏ tới extracted directory riêng của cùng secure workspace. Tool thật phát 101 dòng `Packing:`, stderr rỗng và trả exit code `1` khi pack thành công. Runner chỉ chấp nhận code `1` cho operation Pack, đồng thời vẫn bắt buộc pack progress, keydat hợp lệ và archive artifact tồn tại/non-empty. Extract tiếp tục yêu cầu exit code `0`; mã pack khác `0/1` vẫn fail.

## Baseline

- C#, .NET 10 LTS.
- WinUI 3 trên Windows App SDK cho desktop UI.
- MVVM, Dependency Injection, structured logging.
- Các tác vụ dài dùng `async/await`, `CancellationToken` và progress reporting.
- Release đích là Windows x64; archive runner về sau vẫn phải chạy được `acv.exe` 32-bit.

## Module

| Project | Trách nhiệm | Dependency trực tiếp tại PLAN 01 |
|---|---|---|
| `AuditionModStudio.Core` | Domain contract và model trung tâm | Không có |
| `AuditionModStudio.Infrastructure` | Implementation hạ tầng dùng chung | `Core` |
| `AuditionModStudio.Archives` | Archive abstraction, ACV Tool 5, keydat | `Core` |
| `AuditionModStudio.Dds` | DDS metadata/decode/encode/validation | `Core` |
| `AuditionModStudio.Imaging` | Image import/processing | `Core` |
| `AuditionModStudio.Projects` | Project/workspace lifecycle | `Core` |
| `AuditionModStudio.Mods` | Game, Mod Definition, Texture Manifest | `Core` |
| `AuditionModStudio.AI` | AI abstraction | `Core` |
| `AuditionModStudio.Cloud` | Supabase và trusted backend client | `Core` |
| `AuditionModStudio.Security` | Secure storage và integrity abstraction | `Core` |
| `AuditionModStudio.Updater` | Update abstraction | `Core` |
| `AuditionModStudio.App` | WinUI composition root | Tất cả module trên |

Dependency graph tại nền tảng:

```text
AuditionModStudio.App
  ├─ Infrastructure ─┐
  ├─ Archives ───────┤
  ├─ Dds ────────────┤
  ├─ Imaging ────────┤
  ├─ Projects ───────┤
  ├─ Mods ───────────┼─> Core
  ├─ AI ─────────────┤
  ├─ Cloud ──────────┤
  ├─ Security ───────┤
  └─ Updater ────────┘
```

`Core` không tham chiếu WinUI, Cloud hoặc implementation cụ thể. Cross-module dependency mới chỉ được thêm khi PLAN tương ứng chứng minh là cần thiết; không tạo vòng tham chiếu.

## Quy tắc dữ liệu và orchestration

- Archive không được nhận diện bằng extension hard-code.
- Pristine template bất biến; mọi thay đổi diễn ra trong isolated project working copy.
- Texture identity là normalized relative directory path cộng exact filename.
- UI gọi abstraction qua DI; không gọi process, filesystem engine, DDS engine hoặc provider trực tiếp.
- Build/install dùng validate-before-promote, backup và recovery theo các PLAN tương ứng.

## Application bootstrap

Từ PLAN 02, `AuditionModStudio.App` dùng .NET Generic Host làm lifecycle container:

1. Tạo `AppPaths` trỏ tới `%LocalAppData%\AuditionModStudio`.
2. Khởi tạo file logger trước các service khác.
3. Đăng ký service qua DI.
4. Chạy `IStartupValidator` trước khi hiển thị cửa sổ chính.
5. Khởi động host rồi resolve `MainWindow` từ container.
6. Khi cửa sổ đóng, chặn lần đóng đầu tiên để `StopAsync`, dispose service và flush log; sau đó mới đóng thật.

`App.xaml.cs` chỉ điều phối WinUI lifecycle và global exception events. Filesystem implementation nằm trong `Infrastructure`; contract `IAppPaths` và `IStartupValidator` nằm trong `Core`. Cách phân tách này không gắn business logic với elevation và giữ khả năng chuyển privileged operation sang broker riêng trong tương lai.

PLAN 02 chưa có archive, DDS, cloud, AI, security implementation hay business UI.

## App paths và secure workspace từ PLAN 03

`IAppPaths` là nguồn duy nhất cho các thư mục được quản lý dưới `%LocalAppData%\AuditionModStudio`: `Logs`, `Cache`, `Projects`, `Temp`, `Settings`, `Downloads`, `SecureTemplateCache`, `Backups` và `Temp\Workspaces`. Không module nào được suy ra các path này từ working directory hoặc installation directory.

`IPathSecurity` canonicalize relative path, chuẩn hóa từng segment về Unicode Form C, từ chối rooted/UNC/drive path, navigation segment, Windows reserved device name, alternate data stream và ký tự filename không hợp lệ. Containment được quyết định bằng `Path.GetRelativePath` sau `Path.GetFullPath`, không bằng string prefix.

Mỗi operation nhận một workspace riêng:

```text
Temp\Workspaces\<128-bit-random-id>\
  .workspace.lock
  Working\
  Extracted\
  BuildOutput\
```

`SecureWorkspacePaths` phân tách working archive, extracted files và build output. Pristine template nằm ngoài model writable này, trong `SecureTemplateCache`; PLAN 03 không có API ghi đè pristine template. `ISecureWorkspace` giữ exclusive handle trên `.workspace.lock` đến khi dispose. Workspace tạm chưa commit bị cleanup khi dispose; từ PLAN 33, workspace đã gắn với `.audproj` được retain để dispose/host shutdown chỉ nhả exclusive lock, không xóa dữ liệu project.

Cleanup abandoned workspace chỉ xét direct child có ID đúng định dạng và marker hợp lệ; workspace còn exclusive lock bị bỏ qua. Cleanup không nhận arbitrary path và không đi vào `Projects`, `SecureTemplateCache` hoặc dữ liệu project.

## Settings system từ PLAN 04

`Core` định nghĩa `ISettingsService`, `ISettingsValidator`, `ApplicationSettings` và validation result. `Infrastructure` sở hữu JSON serialization, schema migration boundary, filesystem I/O, atomic promotion và recovery. `App` chỉ đăng ký các service vào DI; `SettingsInitializationService` load settings khi host khởi động.

Schema v1 có các section strongly typed:

```text
ApplicationSettings
  SchemaVersion
  Game.InstallationDirectory
  Tooling.AcvExecutablePath
  Project.DefaultProjectDirectory
  Project.AutoBackupEnabled
  Project.DefaultInstallBehavior
  Appearance.Language
  Appearance.Theme
  Appearance.ThumbnailSize
  Backend.ApiBaseUrl
```

`Cache`, `Temp` và `Temp\Workspaces` không phải settings có thể chỉnh sửa. Các path nội bộ tiếp tục do `IAppPaths` kiểm soát. Game/tool/project path là external configuration và vẫn phải được validate lại theo trust boundary của operation sử dụng chúng.

Settings nằm tại `Settings\settings.json`, backup hữu hạn tại `settings.json.bak`. Save được serialize trong process, validate lại, ghi temp file cùng filesystem, flush xuống disk rồi promote bằng replace/move. Một `SemaphoreSlim` serialize các operation trong singleton service. Schema mới hơn bị từ chối; migration cũ chỉ chạy qua `ISettingsSchemaMigration` được đăng ký rõ ràng.

## Real sample fixture registration từ PLAN 05

Fixture metadata chỉ nằm trong `IntegrationTests`, không đi vào production client. Catalog đăng ký `acv.exe`, `015.ab` và optional sample `samples\private\tn_coby_logo.dds`. DDS sample 6000×1801, DXT5/BC3, 1 mip level chỉ là một quan sát, không phải yêu cầu DDS toàn cục và việc thiếu sample này không làm fail Stage Gate. Source of truth cho DDS về sau là từng file được scan từ working archive đã extract.

Test không nhận arbitrary source path. `RepositoryFixtureLocator` tìm repository root bằng solution marker, sau đó resolve registered relative path qua `IPathSecurity`. `FixtureCopyService` mở source read-only, copy bất đồng bộ vào `SecureWorkspace.Paths.WorkingDirectory`, flush và so sánh SHA-256 trước khi trả working copy. Chỉ working copy được phép mutation.

Fixture có thể không tồn tại trong CI vì proprietary binary/game asset bị loại khỏi Git. Availability được báo tường minh; metadata test vẫn deterministic. Không có tool execution, archive extraction hoặc DDS decoding trong PLAN 05.

## ACV Tool 5 process runner từ PLAN 06

`IArchiveToolRunner` và `AcvTool5Runner` nằm trong module `Archives`, không phụ thuộc UI. Request mang `ISecureWorkspace`, archive/extract path tương đối và executable path tuyệt đối. Runner canonicalize toàn bộ path qua `IPathSecurity`, chỉ cho chạy executable nằm trong working workspace, rồi yêu cầu `IArchiveToolExecutionPolicy` phê duyệt trước launch. PLAN 07 có thể thay policy bằng kiểm tra hash/version mà không sửa process runner.

Process chạy trực tiếp với `UseShellExecute=false`, `CreateNoWindow=true`, redirect stdin/stdout/stderr và dùng `ArgumentList`. Stdout được đọc theo chunk; parser giữ buffer hữu hạn nên nhận được `Select:` kể cả khi không có newline hoặc bị chia giữa nhiều chunk. Khi keydat thiếu, selection từ trusted `GameRegionProfile` chỉ được gửi một lần. Khi keydat có sẵn, runner không chờ prompt.

State/progress có cấu trúc độc lập với UI. Cancellation và timeout kết thúc toàn bộ process tree. Sau exit, runner kết hợp exit code, progress marker và artifact trong workspace để quyết định kết quả; không coi exit code 0 là đủ. PLAN 06 dùng fake child process để kiểm chứng protocol và không chạy real `acv.exe` hay sửa fixture.

## Keydat lifecycle và ACV Tool integrity từ PLAN 07

`IKeydatService` là nơi duy nhất suy ra companion path từ archive basename. Status phân biệt `Missing`, `PresentUnverified` và `Invalid`; file chỉ tồn tại không bao giờ được gọi là valid khi format chưa được hiểu. Keydat nằm cạnh working archive trong từng `ISecureWorkspace`, được giữ lại giữa extract/pack và bị cleanup cùng workspace. Copy từ trusted source dùng source root + relative path đã canonicalize, copy tạm, flush, SHA-256 verification và atomic move; source không bị ghi.

`TrustedArchiveToolManifest.Production` chứa descriptor code-owned cho `acv_tool_5`: filename `acv.exe`, approved SHA-256 `6A52C808D7E5A59EB41E43D86A32E78067F424C8531E34981887F093E81547D3` và optional version metadata. Manifest này không thuộc user-editable settings. `ArchiveToolIntegrityPolicy` kiểm tra tool id, containment, reparse point, filename và streaming SHA-256; version resource chỉ là secondary diagnostic nên việc thiếu version không phủ định hash đúng.

`ArchiveToolProvisioningService` thực hiện `verify trusted source → copy vào isolated Working → verify copy`. `AcvTool5Runner` gọi cùng `IArchiveToolExecutionPolicy` ngay trước `Process.Start`; integrity failure dùng structured reason và không launch process. Hash không cache để tránh stale decision.

Vẫn còn cửa sổ TOCTOU giữa lần hash cuối và Windows mở executable. PLAN 07 giảm rủi ro bằng controlled workspace, reparse rejection, verify source/copy và verify lại ngay trước launch, nhưng chưa có handle-based execution binding hoặc ACL/broker hardening tuyệt đối.

## Audition Archive abstraction từ PLAN 08

`Core` định nghĩa `IAuditionArchiveService` cùng các model `AuditionArchiveTemplate`, `ArchiveEngineType`, `ArchiveWorkspace`, request/result, semantic progress và structured failure. Extension chỉ là metadata của descriptor; `.ab`, `.acv` và extension tương lai đi cùng một luồng. Caller không truyền raw argument, executable path, country selection hoặc trực tiếp thao tác keydat.

Pipeline chính thức:

```text
Project/Caller
  → IAuditionArchiveService
  → IArchiveEngine (resolve bằng explicit EngineId)
  → IArchiveToolProvisioningService
  → IKeydatService
  → IArchiveToolRunner
```

`AuditionArchiveService` mở pristine source chỉ đọc, copy qua file tạm, flush, so sánh SHA-256 rồi promote vào `SecureWorkspace.Paths.WorkingDirectory`. Nếu working archive đã tồn tại, service từ chối overwrite. Extract target nằm dưới `SecureWorkspace.Paths.ExtractedDirectory`; build output tiếp tục tách riêng và chưa được promote ở PLAN 08.

`AcvTool5ArchiveEngine` là implementation của `ArchiveEngineType.AcvTool5` ở module `Archives`. Engine map semantic intent sang runner mà không leak `-da`, `-ca`, `Select:` hoặc `Process`. Provisioning/integrity luôn xảy ra trước keydat inspection và runner launch. Keydat `Missing` được runner xử lý bằng trusted region profile; `PresentUnverified` được reuse; `Invalid` chặn launch.

App composition đăng ký archive service và các low-level security boundary. Concrete ACV engine chỉ được đăng ký khi có approved trusted tool location; PLAN 08 không tự tin một path từ settings hoặc chạy proprietary tool. `015.ab` chỉ là archive mẫu thật, không phải tên hay extension toàn cục. DDS về sau đến từ recursive scan của working archive đã extract; `tn_coby_logo.dds` vẫn chỉ là optional sample.

## Project Archive Workspace từ PLAN 09

`Core` định nghĩa `IProjectArchiveWorkspaceService`, lease `IProjectArchiveWorkspace`, descriptor, template reference và structured create/validation result. `Projects` triển khai lifecycle filesystem; `App` chỉ đăng ký composition qua DI.

Pipeline chuẩn bị project archive:

```text
Pristine Archive Template (read-only)
  → validate path/reparse point/hash
  → SecureWorkspaceService.CreateAsync
  → Working/<exact archive filename>
  → Extracted/<descriptor extract folder>
  → BuildOutput/
  → atomic .project-archive-workspace.json
  → Ready
```

Mỗi create operation nhận `ProjectId` dạng GUID và `WorkspaceId` random độc lập. `DisplayName` chỉ là metadata đã validate, không tham gia directory path. Template reference snapshot giữ `TemplateId`, version, source SHA-256, engine ID và region profile; project cũ không tự động đổi theo template catalog tương lai.

Manifest schema v1 chỉ là recovery metadata tối thiểu cho archive workspace, không phải file project `.audproj`. Nó lưu relative/logical path, không lưu executable hoặc secret. Ghi manifest dùng temp file cùng filesystem, flush-to-disk rồi atomic move. Chỉ sau khi working copy/hash/directories và manifest hoàn tất service mới trả state `Ready`; failure/cancellation dispose lease để `SecureWorkspaceService` cleanup partial tree.

Validation phân biệt manifest missing/corrupt/mismatch, working archive missing/hash mismatch, extracted/build directory missing và invalid path. Không tự sửa âm thầm. Dispose project A chỉ cleanup workspace A; pristine source và workspace B không bị ảnh hưởng.

PLAN 09 dùng randomized lease dưới `Temp\Workspaces`, đúng phạm vi cleanup abandoned temp workspace trong roadmap. Durable project directory dưới `Projects`, `.audproj`, load/recover project lâu dài và reset workflow thuộc PLAN 31–35. Repository-root `015\` không phải production workspace và không được service tham chiếu.

`AuditionArchiveService` có thể reuse working archive đã được PLAN 09 chuẩn bị nếu SHA-256 khớp pristine source; mismatch bị từ chối và không overwrite. Nhờ vậy `ProjectArchiveWorkspace.ArchiveWorkspace` sẵn sàng cho PLAN 10 mà Project layer không biết `acv.exe`, command hoặc keydat internals.

## Real ACV extract Gate A từ PLAN 10

Integration test Windows/private-fixture chạy đúng production chain:

```text
IProjectArchiveWorkspace
  → IAuditionArchiveService
  → AcvTool5ArchiveEngine
  → AcvTool5Runner
```

`015.ab` và approved `acv.exe` chỉ được dùng sau khi hash pristine khớp, rồi được copy/provision vào randomized workspace có path Unicode và khoảng trắng. Real extract dùng working archive và ghi vào `Extracted\015`; test chỉ tạo inventory đếm tổng hợp, không tạo asset scanner của PLAN 11.

Behavior thực tế bổ sung cho process protocol: binary ACV Tool 5 này block-buffer stdout khi chờ stdin, nên runner phải pre-seed trusted AuditionVN selection cho keydat `Missing` thay vì chỉ đợi parser thấy `Select:`. Ngoài ra, keydat vừa được tool tạo có trạng thái `PresentUnverified` và hash trùng sample vẫn khiến binary hỏi country ở lần chạy kế tiếp. Runner cho nhánh này một grace period để process có thể tự hoàn tất; chỉ khi process vẫn đứng mới dùng cùng trusted selection làm liveness fallback. Fake regression vẫn chứng minh trường hợp existing keydat tự hoàn tất không nhận selection.

Output thật giữ đúng casing/spacing `writing :`; `Select:` không kết thúc ngay bằng newline mà theo sau bởi một khoảng trắng. Cả hai real extract exit `0`, tạo 101 file trong 7 thư mục và được cleanup cùng project workspace. Đây là Gate A cho extract; không bao gồm pack, DDS decode hoặc semantic scan.

## DDS Metadata Reader từ PLAN 13

Pipeline texture hiện tại là:

```text
Archive → Extract → Asset Scanner → IDdsMetadataReader → DdsMetadata
```

`Core` sở hữu contract `IDdsMetadataReader`, structured result/failure và model metadata strongly typed. `AuditionModStudio.Dds` triển khai parser managed, little-endian, read-only; không phụ thuộc UI, Projects hay native DDS library. Reader chỉ đọc tối đa 148 byte header cần thiết, không load payload theo dimensions trong header.

Legacy header và `DDS_PIXELFORMAT` được validate lần lượt với size 124 và 32. FourCC hỗ trợ `DXT1`, `DXT3`, `DXT5`, `ATI1`, `ATI2`, `BC4U/S`, `BC5U/S` và `DX10`. DX10 header expose numeric DXGI format, resource dimension, cubemap, array size và nhận diện BC1–BC7 cùng RGBA8/BGRA8 phổ biến. Format chưa biết vẫn trả metadata với `Unknown`/`Unsupported`, không crash.

`DeclaredMipMapCount` giữ nguyên giá trị header; `EffectiveMipLevelCount` là 1 khi declared count bằng 0 để biểu diễn base level. Alpha chỉ là khả năng/channel theo format; không khẳng định pixel thực tế có sử dụng alpha. Legacy color space là `Unknown`; chỉ DXGI `_SRGB` được ghi `Srgb`.

Metadata reader không phải DDS decoder, encoder, thumbnail renderer hay image converter. `PixelDataOffset` chỉ là 128 cho legacy hoặc 148 cho DX10; PLAN 13 không đọc pixel data.

## DirectXTex Evaluation Harness từ PLAN 14

`Core` định nghĩa `IDirectXTexEvaluationHarness` và request/result/progress có cấu trúc. `AuditionModStudio.Dds` triển khai harness bằng Microsoft `texconv` được pin version/hash. Harness chỉ là prototype và compatibility oracle, không phải `IDdsPreviewService` hoặc production encoder.

Pipeline evaluation:

```text
ISecureWorkspace input
  → managed metadata/PNG-header preflight + size/pixel policy
  → verify source texconv SHA-256
  → copy + verify trong Working/DirectXTexEvaluation
  → structured process arguments
  → output tại BuildOutput/<evaluation-id>
  → IDdsMetadataReader hoặc PNG-header post-validation
```

UI không tham chiếu harness. Kết quả, command/API và quyết định native-wrapper cho PLAN sau được ghi tại `docs/DIRECTXTEX_EVALUATION.md`.

## DDS Preview Service từ PLAN 15

`Core` định nghĩa `IDdsPreviewService`, request/result/failure và `DdsPreviewImage` bất biến. Representation là PNG encoded trong memory (`image/png`) kèm width/height; không chứa `BitmapImage`, XAML object, WinUI control, raw stride hoặc layout channel cần caller suy đoán.

Pipeline production preview:

```text
DDS trong ISecureWorkspace
  → IDdsMetadataReader preflight
  → DdsPreviewResourcePolicy
  → controlled DirectXTex decode mip 0
  → PNG IHDR/dimension/byte-length validation
  → immutable DdsPreviewImage
  → caller/UI adapter/cache consumer tương lai
```

`DdsPreviewService` dùng `DirectXTexEvaluationHarness` như process boundary đã được pin của PLAN 14, nhưng không expose harness hoặc tool path trong preview request. Đường dẫn tool production là cấu hình DI code-owned dưới application base directory; mỗi request dùng output directory GUID riêng dưới `BuildOutput`, đọc kết quả vào memory rồi cleanup trong `finally`. Service stateless, chạy bất đồng bộ, hỗ trợ cancellation/finite timeout và không sửa source.

PLAN 15 chỉ decode full-resolution base mip; không resize/downscale âm thầm. Preview Service không phải production encoder, image editor hay disk thumbnail cache. Encoder vẫn thuộc PLAN 16; UI adapter/cache thuộc PLAN phù hợp sau.

## Image → DDS Encoder từ PLAN 16

`Core` định nghĩa `IDdsEncoder`, immutable internal `DdsRgbaImage` và explicit `DdsTargetSettings`. Image contract ghi rõ width, height, stride, `Rgba8` và buffer; settings bắt buộc format, mip count, header type, color space và alpha semantics. Không tồn tại Audition default format hoặc overload tự chọn BC3.

Pipeline production encode:

```text
Internal RGBA8 image
  → stride/buffer/dimension/alpha/mip/resource validation
  → explicit DdsTargetSettings
  → temporary PNG bridge trong Working
  → controlled DirectXTex encode trong isolated BuildOutput operation
  → IDdsMetadataReader verification
  → atomic move tới controlled BuildOutput relative path
```

Format production hiện hỗ trợ đúng tập đã quan sát: BC1, BC3, RGBA8 và BGRA8. Linear hỗ trợ legacy/DX10; sRGB chỉ hỗ trợ DX10 vì legacy header không lưu semantic sRGB. BC1 chỉ nhận opaque hoặc binary alpha; full alpha bị từ chối. Encoder giữ exact dimensions, exact requested mip count và reject input/target dimension mismatch thay vì resize.

Encoder không phải resize engine, Match Original orchestrator, archive replacement hoặc pack workflow. PLAN 17 mới map metadata target thành settings ở project flow; PLAN 16 chỉ cung cấp contract đủ explicit để orchestration đó gọi an toàn.

## Match Original DDS từ PLAN 17

`IDdsMatchOriginalService` orchestration đúng ba abstraction đã có: `IDdsMetadataReader` đọc target, centralized `DeriveProfile` tạo `DdsMatchOriginalProfile`/`DdsTargetSettings`, rồi `IDdsEncoder` tạo output mới. Service đọc lại output và trả `DdsMetadataMatchReport`; UI không tự so metadata hoặc parse diagnostic text.

```text
Target DDS trong secure workspace
  → metadata preflight
  → strict match profile
  → immutable replacement RGBA8
  → IDdsEncoder
  → metadata post-validation
  → matched DDS mới trong BuildOutput
```

Strict profile giữ exact format, dimensions, effective mip count, header và resource shape. `DeclaredMipMapCount=0`/`EffectiveMipLevelCount=1` được encode thành đúng một base mip. Legacy `Unknown` color space được lưu trong profile nhưng encoder dùng legacy-compatible Linear command policy, không suy đoán sRGB; DX10 Linear/sRGB được preserve khi meaningful.

BC1 profile luôn giữ BC1 và binary-alpha capability; replacement có semi-transparent alpha bị từ chối, không nâng thành BC3. BC7, cubemap, array và volume được reject có cấu trúc vì encoder hiện chưa hỗ trợ. Match Original không có compatibility mode đổi format.

Match Original nghĩa structural metadata fidelity, không phải byte-identical hash hoặc exact file size. PLAN 17 không resize, replace extracted target, pack archive hoặc install mod.

## DDS Validation từ PLAN 18

`IDdsValidationService` là gate độc lập giữa DDS đã encode và mọi replacement workflow tương lai. Request chỉ mang một `ISecureWorkspace` cùng relative path của target/candidate; service mở lại cả hai file qua `IDdsMetadataReader`, không tin metadata được trả về từ encoder và không mutate file nào.

```text
Target DDS (read-only) + encoded candidate (read-only)
  → path/reparse-point validation
  → IDdsMetadataReader cho từng file
  → supported-profile gate
  → exact structural metadata comparison
  → PASS | structured failure + field report
```

Comparison bắt buộc gồm format, dimensions, effective mip count, header type, meaningful DX10 color space và resource shape. Legacy `Unknown` color space không bị suy diễn thành sRGB. Hash/file size không được so như compatibility contract vì BC compression/serialization có thể khác nhau.

`DdsMatchOriginalService` reuse validator này sau `IDdsEncoder`; output chỉ được trả thành công khi validation PASS. PLAN 18 vẫn không replace extracted target, không rollback/archive-pack và không mở rộng support sang BC7, volume, array hoặc cubemap. Workflow Apply/replace tương lai phải coi `DdsValidationResult.Succeeded` là precondition bắt buộc.

## Internal Image Model & Import từ PLAN 20

`IImageImportService` là boundary UI-neutral giữa file do người dùng chọn và image processing. Request nhận absolute local path, implementation mở read-only, nhận diện signature thực và chỉ cho phép PNG, JPEG, WebP hoặc BMP. Extension không quyết định decoder. UI file picker, DDS settings và archive logic không đi vào service.

```text
User image (read-only)
  → signature + regular-path validation
  → SKCodec metadata probe
  → dimension/pixel/decoded-byte policy
  → RGBA8888 straight-alpha decode + EXIF orientation normalization
  → immutable InternalImage
  → future image processing → DdsRgbaImage/IDdsEncoder
```

`InternalImage` dùng packed `RGBA8Straight`: channel order R-G-B-A, 8 bit/channel, stride luôn `width × 4`, immutable owned `ImmutableArray<byte>`. Constructor defensive-copy tại import trust boundary; `DdsRgbaImage.Create(InternalImage)` reuse cùng immutable storage nên không tạo conversion/channel-copy thứ hai. Buffer chỉ chứa managed memory nên không cần caller dispose; mọi `SKCodec`, stream, bitmap và color-space native object được service dispose trước khi trả kết quả.

Skia decode vào explicit sRGB output color space. Embedded profile được decoder áp dụng khi có thể; model chỉ giữ cờ codec-reported và không giữ EXIF/ICC blob. EXIF orientation 1–8 được normalize vào pixels và normalized dimensions. Animated/multi-frame input chỉ lấy frame đầu trong scope PLAN 20; resize/crop/editor document/DDS replacement không thuộc model này.

## Arbitrary Resize Engine từ PLAN 21

`IImageResizeService` chỉ nhận `InternalImage`, target dimensions và strongly typed options; không đọc/ghi file, không biết encoded image format, DDS metadata, archive hoặc UI. Output tiếp tục là immutable packed `RGBA8Straight` `InternalImage`.

```text
InternalImage
  → target/options preflight
  → straight RGBA premultiply
  → direct Skia pixel resize/crop/canvas operation
  → unpremultiply
  → new InternalImage
```

Modes có semantic cố định: Stretch/Free Aspect tạo exact target không giữ aspect; Fit giữ aspect và letterbox transparent vào exact canvas; Fill giữ aspect và crop theo alignment; Keep Aspect trả fitted dimensions trong requested bounding box; Manual Crop resize rectangle hợp lệ vào exact target; Canvas Resize đặt source không scale và cho phép clip/pad; Transparent Padding chỉ pad và reject canvas nhỏ hơn source. Alignment công khai gồm center/top/bottom/left/right.

Filter công khai tối thiểu là Nearest Neighbor và Linear, default Linear; library default không được dùng. Filtering diễn ra theo sRGB channel behavior, chưa phải gamma-linear/perceptual resize. Engine premultiply alpha trước filter và unpremultiply trước output để không gắn nhãn premultiplied data thành straight alpha hoặc tạo black fringe ở transparent edge.

Target dùng chung policy PLAN 20: dimension 16384, 100 triệu pixel và 512 MiB decoded RGBA, checked trước native allocation. Same-size Stretch/Free Aspect trả lại cùng immutable source; các operation khác không mutate source. PLAN 21 không phải interactive crop/transform model, editor, AI upscaler hoặc DDS/archive orchestration.

## Interactive Crop/Transform Model từ PLAN 22

`IImageTransformService` là pure, stateless geometry boundary. Canonical crop state dùng `NormalizedImageRectangle` trong `[0,1]`; viewport logical coordinates, image pixel coordinates và normalized coordinates có value types riêng. State là immutable record và chỉ giữ image dimensions, không giữ/copy `InternalImage.Pixels`.

```text
InternalImage dimensions
  → immutable InteractiveImageTransformState
  → normalized crop + derived viewport projection
  → deterministic ImageCropRectangle
  → PLAN 21 Manual Crop request khi cần execute pixels
```

Crop bounds dùng half-open semantics. Interactive crop bị clamp vào image; zero/outside/minimum crop bị reject. Custom finite positive aspect ratio được áp theo actual image pixels với center/top-left/top-right/bottom-left/bottom-right anchor. Final integer crop tập trung một policy: floor left/top, ceil right/bottom, rồi clamp vào image.

Transform order explicit: flip/scale quanh image center → quarter-turn rotation → image-space translation → fit/letterbox viewport → zoom quanh viewport center → viewport pan. Inverse mapping đảo cùng matrix. Rotation chỉ 0/90/180/270; pan dùng viewport logical units, translate dùng image pixels. Zoom mặc định giới hạn 0.1–32; pan không clamp vì roadmap không đặt visibility constraint.

Reset trả identity state. Viewport resize chỉ derive projection mới, không quantize hoặc mutate normalized crop. Model không biết DPI, XAML, pointer event, Canvas/Skia UI, không resample pixels và không triển khai undo/redo.

## Image Adjustments Core từ PLAN 23

`IImageAdjustmentService` là boundary stateless, UI-neutral nhận và trả cùng canonical `InternalImage`. Service không đọc/ghi file, không gọi process, không biết DDS/archive và không expose type Skia. Neutral settings trả lại chính immutable source; mọi adjustment khác tạo `InternalImage` mới cùng dimensions, packed stride và source metadata.

```text
InternalImage RGBA8 straight-alpha sRGB
  → validate finite/range/resource policy
  → deterministic managed adjustment pipeline
  → centralized clamp + midpoint-away-from-zero quantization
  → immutable InternalImage RGBA8 straight-alpha sRGB
```

Range contract: brightness/contrast/saturation/vibrance/temperature/tint/highlights/shadows `[-1,1]`; exposure `[-5,5] EV`; hue `[-180,180]°`; gamma `[0.1,10]`; sharpen `[0,1]`; blur `[0,20] px`; opacity `[0,1]`. NaN, Infinity và out-of-range bị reject có cấu trúc, không silent clamp parameter.

Processing order cố định là brightness → contrast → exposure → saturation → vibrance → hue → temperature → tint → highlights → shadows → gamma → sharpen → blur → opacity. Brightness là offset `value × 255`; contrast dùng midpoint 127.5 và factor `1 + value`; exposure dùng `2^EV`; saturation dùng Rec.709 luma coefficients; vibrance dùng chroma-adaptive saturation; hue dùng deterministic luma-preserving RGB rotation. Temperature/tint và highlight/shadow là transform sRGB-channel có semantic đơn giản, không được mô tả là physical white balance hoặc tone-mapping engine. Gamma là arbitrary `channel^(1/gamma)`, không phải sRGB transfer conversion.

Sharpen dùng four-neighbor unsharp kernel; blur dùng separable box blur với radius tối đa 20 và fractional blend. Math chạy trực tiếp trên sRGB channels, chưa phải linear-light/ICC pipeline. Hidden RGB của fully-transparent pixel vẫn được adjust deterministic. Tất cả operation trừ opacity giữ alpha byte exact; opacity chỉ nhân alpha, không đổi RGB. Service kiểm tra cancellation theo row/column, không trả partial image, và concurrent call không dùng shared mutable state.

## Edit History / Undo Redo từ PLAN 24

`IEditHistoryService` là stateless factory tạo một `IEditHistorySession` riêng cho từng editor. Session giữ linear operation history độc lập WinUI và serialize mọi mutation bằng private lock. Public state, entry và stack view đều immutable; Core không dùng `ICommand`, XAML, Dispatcher, localized label, filesystem hoặc process.

```text
ImageEditorState
  ├─ immutable InternalImage reference
  ├─ InteractiveImageTransformState
  └─ ImageAdjustmentSettings
       → EditHistorySession
       → stable RevisionId
       → Undo / Redo
```

Snapshot strategy là hybrid. Transform/crop và non-destructive adjustment chỉ snapshot lightweight state, reuse cùng `InternalImage`; resize/destructive image state giữ exact immutable image reference để undo không dùng lossy inverse. History memory accounting tính mỗi image buffer một lần theo reference identity cộng estimated entry overhead 512 bytes. Default budget là 100 entries và 256 MiB; configurable options vẫn bị hard-cap ở 10.000 entries/4 GiB. Budget bao gồm current image cùng mọi unique image được history giữ. Khi vượt budget, oldest undo entry bị evict; current state không bị evict. Nếu một entry mới vẫn không vừa sau eviction, commit bị reject atomically.

Revision tăng đơn điệu và không reuse sau khi redo branch bị discard. Saved checkpoint lưu exact revision; `IsDirty` so current/saved revision và phản ánh pending transaction update. Standard linear behavior áp dụng: undo chuyển latest entry sang redo; redo phục hồi exact after-state; edit mới sau undo xóa toàn redo branch.

Transaction hỗ trợ `Begin → Update* → Commit | Cancel` để coalesce slider hoặc pointer drag. Intermediate update thay preview state nhưng không tăng revision hoặc tạo entry. Commit tạo đúng một entry; cancel phục hồi exact before-state. Nested transaction và push/undo/redo/save/clear trong transaction bị reject có cấu trúc. History chỉ ghi state sau khi caller đã thực hiện edit thành công; PLAN 24 không replay operation, persist project history hoặc cung cấp UI command.

## Alpha Channel Utilities từ PLAN 25

`IAlphaChannelService` là boundary stateless, UI-neutral làm việc trực tiếp trên canonical immutable `InternalImage`. Service không đọc/ghi file, không biết DDS/archive/UI và không thêm native dependency. Năm operation đúng roadmap là View, Extract, Replace, Invert và Threshold.

```text
InternalImage RGBA8 straight-alpha sRGB
  → validate operation/resource/numeric input
  → view | extract | replace | invert | threshold
  → InternalImage hoặc immutable AlphaChannelData
```

View tạo `InternalImage` grayscale opaque với `R=G=B=source A`, `A=255`. Extract tạo channel plane một byte/pixel, stride bằng width; đây là dữ liệu kênh, không phải image pixel model thay thế. Replace yêu cầu channel có exact dimensions. Invert dùng `A' = 255 - A`. Threshold dùng rule cố định `A >= threshold → 255`, `A < threshold → 0`, threshold nguyên trong `0..255`.

Replace, Invert và Threshold chỉ thay alpha; RGB, kể cả hidden RGB tại pixel fully transparent, được giữ byte-exact. Không premultiply/unpremultiply trong utility nên output vẫn straight alpha. Nếu Replace/Threshold không đổi byte alpha nào thì immutable source được reuse. Managed loops kiểm tra cancellation theo row, không publish partial output và không dùng mutable state dùng chung. Alpha edit có `EditOperationKind.Alpha` để orchestrator tương lai lưu before/after image references; service không tự ghi history.

## Game Catalog từ PLAN 26

`Core` định nghĩa stable value object `GameId`, immutable `GameDefinition` và read-only contract `IGameCatalog`. `AuditionModStudio.Mods` cung cấp `GameCatalog`; composition root tạo và validate built-in catalog ngay khi bootstrap rồi đăng ký singleton. Catalog hiện có đúng game đầu tiên với ID machine-friendly `audition` và display metadata `Audition`.

```text
Code-owned built-in definitions
  → validate IDs/null/empty/duplicate
  → deterministic sort bằng GameId ordinal
  → immutable GameCatalog singleton
  → GetGames | TryGetGame
```

`GameId` chỉ nhận 1–64 lowercase ASCII letter/digit/underscore/hyphen, bắt đầu bằng letter/digit; display name hỗ trợ Unicode/khoảng trắng nhưng không tham gia identity. `GetGames` trả immutable snapshot đã sort; `TryGetGame` coi unknown/default ID là expected miss, không ném `KeyNotFoundException`. Provider không đọc file/settings/network và không expose mutable dictionary/list nên concurrent reads không cần lock.

PLAN 26 cố ý không đặt archive filename, source path, template/version/hash, engine, region, country selection hoặc install path trong `GameDefinition`. Các relationship `Game → Mod Type → Archive Template → Engine/Region` thuộc `ModDefinition` của PLAN 27; cách tách này ngăn screen dùng Game Catalog để hard-code `015.ab` trước khi mapping semantic được định nghĩa đúng PLAN.

## Mod Definition từ PLAN 27

`Core` định nghĩa immutable `ModDefinition`, stable `ModId`, `ModCategory`, validated `ModRelativePath`, `ModKeydatStrategy` và read-only `IModCatalog`. `AuditionModStudio.Mods` cung cấp `ModCatalog`; identity của mod là cặp `(GameId, ModId)`, không phải display name, archive filename hoặc convention prefix. `GameId` và `ModId` dùng chung lowercase ASCII validation policy 1–64 ký tự.

```text
IGameCatalog
  → GameDefinition
  → ModCatalog.GetMods(GameId) / TryGetMod(GameId, ModId)
  → ModDefinition
       ├─ semantic display/category/cover/description
       ├─ AuditionArchiveTemplate id + version + filename + extract folder
       ├─ explicit ArchiveEngineType
       ├─ RegionProfileId → IGameRegionProfileResolver
       ├─ ReuseOrGenerate keydat strategy
       └─ validated install-relative path + compatibility information
```

`ModCatalog.Create` kiểm tra dependency, null definition, unknown game, unknown region và duplicate `(GameId, ModId)`, rồi chỉ publish catalog khi toàn bộ input hợp lệ. Danh sách mỗi game sort ordinal theo `ModId`; dictionary và snapshot đều immutable nên concurrent read không cần lock. Empty catalog hợp lệ vì master roadmap PLAN 27 không cung cấp đủ semantic name/category/cover/install/compatibility để định nghĩa một built-in Mod Type mà không suy đoán. Composition root vẫn đăng ký đúng một singleton rỗng đã validate; definition code-owned sẽ được thêm khi có metadata có thẩm quyền.

`AuditionArchiveTemplate` được reuse nguyên trạng; engine được map explicit và không infer từ `.ab`/`.acv`. `GameRegionProfile` cùng `IGameRegionProfileResolver` được đặt tại `Core.Archives` để Mods chỉ phụ thuộc Core; implementation `GameRegionProfileCatalog` vẫn thuộc Archives. `ModDefinition` chỉ giữ `RegionProfileId`, không expose ACV raw country selection. Archive engine mới vẫn được resolution tại archive boundary. PLAN 27 không đọc template file, không detect game install, không tạo UI và không nối project lifecycle của PLAN 30.

## Texture Manifest từ PLAN 28

Quan hệ domain được mở rộng theo chuỗi `Game → ModDefinition → TextureManifest → TextureSlot`. Mỗi manifest gắn explicit với `(GameId, ModId)`; mỗi slot có semantic `TextureSlotId` riêng và ánh xạ tới một `ModRelativePath` DDS đã normalize. Display name không phải identity. Catalog reject atomically manifest của mod không tồn tại, duplicate manifest, duplicate slot ID và asset path collide theo `OrdinalIgnoreCase`, nhất quán với collision semantics của scanner trên Windows.

Slot chỉ chứa metadata được roadmap yêu cầu: display name, typed category, description, immutable tags, preview/editable flags và typed recommended edit mode. Manifest không chứa DDS format, dimensions, mip count hoặc header; `IDdsMetadataReader` tiếp tục là source of truth cho file thật. Không có required/optional semantics hoặc manifest version trong PLAN này.

`TextureManifestCatalog` là immutable code-owned metadata. Lookup thiếu manifest hoặc mapping trả fallback gồm normalized relative path và raw filename, không scan filesystem và không đoán semantic metadata. Production catalog hiện rỗng có chủ ý vì chưa có authoritative texture mapping. PLAN 28 không triển khai smart scan PLAN 29, thumbnail generation, editor state, DDS conversion, replacement hoặc archive execution.

## Smart Mod Scan từ PLAN 29

`ISmartModScanService` nhận typed `(GameId, ModId)` và một `IProjectArchiveWorkspace` đã extract. `SmartModScanService` không enumerate filesystem lần hai mà gọi `IArchiveAssetScanner`, giữ nguyên full observed catalog, lọc `TextureAsset` cho texture pipeline, rồi xử lý tuần tự theo relative path để giới hạn peak memory.

```text
ModDefinition + optional TextureManifest + extracted project workspace
  → IArchiveAssetScanner
  → each observed TextureAsset
       → IDdsMetadataReader (runtime source of truth)
       → IDdsPreviewService → immutable PNG memory
       → IImageImportService.ImportMemoryAsync → IImageResizeService
       → bounded InternalImage thumbnail
       → exact ITextureManifestCatalog resolution
  → deterministic folder groups + missing slots + raw/unknown assets
```

Exact manifest mapping dùng normalized full relative path với Windows `OrdinalIgnoreCase`; không filename-only, fuzzy, similarity hay category inference. DDS ngoài manifest vẫn xuất hiện với raw filename/path, `UnknownSemantics=true` và `CanBeLabeled=true`. Slot khai báo nhưng không observed nằm trong informational `MissingManifestSlots`, không làm scan fail vì PLAN 28 không có required/optional semantics. Output chỉ được publish khi scanner, metadata và thumbnail cho mọi DDS đều thành công; cancellation/failure không trả partial catalog.

Thumbnail tối đa 256 mặc định, hard-cap 1024. Decode reuse controlled preview boundary; PNG được chuyển thẳng qua immutable memory import và resize, không tạo bridge file riêng. Preview implementation có thể dùng isolated temporary `BuildOutput` operation theo PLAN 15 và cleanup trong `finally`; Smart Scan không sửa extracted files, archive hoặc manifest. Production manifest rỗng được hỗ trợ: toàn bộ DDS trở thành raw unknown assets. Admin labeling persistence/UI, editor, replacement/repack và Template Versioning PLAN 30 không thuộc PLAN 29.

## Template Versioning từ PLAN 30

Template có canonical identity bất biến `TemplateId + TemplateVersion + TemplateSha256 + CompatibleGameBuild`. `TemplateVersion` là opaque, bounded ASCII identifier: catalog không áp đặt SemVer, không so sánh lớn/nhỏ và không suy ra "latest" từ chuỗi. Mỗi `TemplateId` có thể chứa nhiều version nhưng phải đánh dấu đúng một `IsCurrent` explicit; lookup exact luôn dùng cặp `(TemplateId, TemplateVersion)`.

```text
Trusted TemplateVersionCatalog
  ├─ exact (TemplateId, TemplateVersion) → AuditionArchiveTemplate
  └─ current TemplateId → explicitly marked version

Project ArchiveTemplateReference
  → exact TemplateId + version + SHA-256 + compatible game build snapshot
  → Resolve(snapshot)
       ├─ ExactMatch
       ├─ CurrentVersionDiffers (không suy ra upgrade/downgrade, không mutate/rebind)
       └─ structured missing/hash/build/legacy-invalid status
```

`AuditionArchiveTemplate` vẫn giữ filename, source-relative path, engine, region và extract-folder mapping của archive boundary, đồng thời expose typed identity PLAN 30. `ArchiveTemplateReference` trong project lưu exact identity; project creation mới từ chối template thiếu version/hash/build. Manifest schema 1 đọc được field build bị thiếu từ dữ liệu legacy nhưng không tự điền current/default: identity đó được xem là invalid cho resolution và không bao giờ silently migrate.

`TemplateVersionCatalog` immutable, construction atomic và đăng ký singleton. Catalog production hiện rỗng có chủ ý vì chưa có authoritative built-in template metadata. Version và SHA-256 là hai trục riêng: cùng version khác hash là conflict/mismatch, không phải version mới. Compatible game build dùng exact ordinal equality. Vì version không có ordering, `CurrentVersionDiffers` cố ý không kết luận upgrade hay downgrade; migration execution, project model `.audproj`, UI prompt, network/download và external manifest không thuộc PLAN 30.

## Project Model `.audproj` từ PLAN 31

`AuditionProject` là immutable aggregate và là schema domain duy nhất cho file `.audproj`. Model lưu `ProjectId`/name, typed `(GameId, ModId)`, exact `TemplateIdentity`, logical workspace reference, edited texture records, image/AI asset references, durable edit/history references, build snapshot, timestamps và schema version. Collection được copy sang `ImmutableArray`, sort deterministic và chỉ publish khi toàn graph hợp lệ.

```text
AuditionProject schema v1
  ├─ project/game/mod + exact template identity
  ├─ workspace ID + relative working archive/extracted root
  ├─ edited texture identities → current image asset references
  ├─ image assets + AI assets (relative path + content SHA-256)
  ├─ edit state/history (revision + before/after asset references)
  └─ build state + timestamps
```

`Sha256Digest` centralize generic content-hash validation; `TemplateSha256` tiếp tục là typed template-specific wrapper. Relative references reuse `ModRelativePath` canonical semantics và không chứa absolute path. `ProjectAssetId` là stable machine ID, không phải filename/display name. Validation reject unsupported schema, default identity, malformed workspace reference, Windows path collision, duplicate asset ID/path, dangling texture/history asset reference, invalid revision graph, inconsistent build artifact và timestamp đảo ngược.

PLAN 31 chỉ định nghĩa aggregate/validation; chưa ghi hoặc load filesystem, chưa tạo workspace, extract/scan/save workflow, recovery, Texture State Machine hay reset. Runtime `IProjectArchiveWorkspace`, thumbnail pixels và process/tool path không được serialize vào model.

## Create Project Workflow từ PLAN 32

`IProjectCreationService` điều phối đúng selection `(GameId, ModId, ProjectName)`; UI không truyền template path, archive engine, region selector hoặc entitlement flag. Workflow resolve trusted catalogs trước, gọi `ITemplateEntitlementService`, acquire exact template qua `IProjectTemplateAcquisitionService`, xác minh region, rồi reuse project workspace/archive/Smart Scan boundaries hiện có.

```text
Game + Mod + Name
  → trusted game/mod lookup
  → entitlement decision → exact template acquisition
  → IProjectArchiveWorkspaceService (verified working copy)
  → ReuseOrGenerate keydat policy inside archive engine
  → IAuditionArchiveService.ExtractAsync
  → ISmartModScanService (scanner + DDS metadata + manifest)
  → ProjectMetadataCache
  → AuditionProjectStore (.audproj)
```

Workflow có typed phase/progress, cancellation và structured failure. Chỉ sau extract + complete Smart Scan + atomic metadata cache + atomic `.audproj` save mới trả success cùng live workspace lease. Mọi failure sau workspace allocation sẽ xóa exact project/cache ID và dispose workspace; rollback failure được báo riêng, không che thành success.

`AuditionProjectStore` dùng filename `<ProjectId:N>.audproj` dưới managed `Projects`; project name không tham gia path. `ProjectMetadataCache` lưu full observed DDS metadata dưới managed `Cache/ProjectMetadata`, sort theo normalized path và reject Windows collision. Cả hai ghi temp cùng filesystem, flush-to-disk rồi atomic replace. Production DI đăng ký provider entitlement/acquisition fail-closed vì chưa có authoritative premium/template distribution; không tự tin local setting hoặc network endpoint. PLAN 32 chưa load/recover project, re-extract policy, Texture State Machine hay reset.

## Load/Recover Project từ PLAN 33

`IProjectLoadService` load exact `<ProjectId:N>.audproj`, reject JSON lạ/schema không hỗ trợ/model sai, sau đó resolve exact template `(TemplateId, Version)` và bắt buộc hash + compatible build khớp snapshot. Không fallback sang current template và không infer từ archive filename.

Luồng recovery phân biệt rõ:

1. Workspace + manifest + working archive hợp lệ, cache hợp lệ: trả project ngay, không extract và không scan.
2. Workspace hợp lệ nhưng cache thiếu/hỏng: chỉ chạy Smart Scan để dựng lại observed DDS metadata cache.
3. Workspace thiếu/không hợp lệ: kiểm tra entitlement, acquire lại exact trusted template, tạo workspace cùng `ProjectId`, extract, scan/cache và atomic-save workspace reference mới.

`ISecureWorkspaceRecoveryService` chỉ mở direct managed child có lowercase-hex ID, marker `version=1`, cấu trúc thư mục đầy đủ và không reparse point. Project workspace được retain chỉ sau khi `.audproj` save thành công; nhờ đó rollback workspace mới vẫn xóa atomically, cò close/reopen project chỉ nhả/tái chiếm lock. PLAN 33 không triển khai Texture State Machine, reset hay thumbnail cache.

## Texture State Machine từ PLAN 34

`ITextureStateMachine` là pure domain evaluator, nhận immutable `AuditionProject`, normalized `ModRelativePath` và explicit runtime observation; service không đọc filesystem hay DDS header. Sáu state typed là `Original`, `Modified`, `AiGenerated`, `Pending`, `Invalid`, `Missing`.

Thứ tự quyết định deterministic: asset không tồn tại → `Missing`; metadata không valid → `Invalid`; operation đang chạy → `Pending`; edited texture tham chiếu AI asset → `AiGenerated`; edited texture tham chiếu imported image asset → `Modified`; không có edit record → `Original`. Lookup path theo Windows collision semantics, cò display name/filename không drive state. Optional previous state chỉ dùng báo transition, không mutate project và không thay edit history. PLAN 34 không reset file/project, encode/replace DDS, cache thumbnail hay tự poll filesystem.

## Reset Texture / Reset Project từ PLAN 35

`IProjectResetService` luôn resolve exact template version/hash/build, kiểm tra entitlement + trusted acquisition + region, sau đó extract một fresh project workspace qua existing archive boundary. Không đọc texture trực tiếp từ global template và không mutate pristine source.

Reset operations được serialize qua async cancellation-aware gate của singleton service để hai transaction không ghi chồng cùng workspace/project snapshot.

Reset một texture dùng `IProjectTextureRestoreService` copy exact normalized relative asset từ extracted tree của disposable pristine workspace sang current project workspace. Copy dùng temp + durable flush + atomic move và giữ backup transaction; scan/model/save fail thì rollback bytes cũ. Khi commit, edit/history của texture đó bị loại, asset không còn reference bị prune, revision tăng và build state thành `Dirty`.

Reset Project tạo fresh workspace cùng `ProjectId`, extract + Smart Scan, tạo project snapshot rỗng edits/assets/history và `NotBuilt`. Fresh workspace được retain trước atomic `.audproj` save; save fail thì explicit removal fresh workspace, save thành công mới remove exact previous workspace. Cache, old-workspace cleanup hoặc temporary-backup cleanup fail sau commit được trả bằng recovery flags, không hạ success thành failure giả sau khi project đã commit. PLAN 35 không có thumbnail cache/UI và không triển khai PLAN 36.

## Thumbnail Cache từ PLAN 36

`IThumbnailCache` là boundary duy nhất để Smart Scan lấy thumbnail. Cache key được dẫn xuất deterministic từ schema cache, SHA-256 nội dung DDS nguồn và `MaximumDimension`; đường dẫn, filename, display metadata và timestamp không tham gia identity. Do đó cùng nội dung và cùng kích thước có thể dùng chung thumbnail, còn thay đổi bytes hoặc kích thước luôn tạo key mới.

```text
Smart Scan observed DDS + content SHA-256
  → IThumbnailCache
      ├─ bounded immutable memory cache
      ├─ managed Cache/Thumbnails/v1 disk cache
      └─ miss/corrupt → IDdsPreviewService → IImageImportService → IImageResizeService
```

Disk entry dùng schema/version và tự mô tả source hash, requested dimension cùng immutable RGBA image metadata. Đọc entry phải kiểm tra đầy đủ header, enum, dimensions, stride, pixel length và exact file length. Entry hỏng là derived data: bị loại và tạo lại, không làm thay đổi DDS nguồn. Publish dùng temporary file cùng thư mục, durable flush rồi atomic move; cancellation không publish partial entry. Memory/disk có giới hạn cấu hình, disk eviction deterministic theo lần truy cập và chỉ chạm file cache do ứng dụng quản lý.

Các request đồng thời cùng key được gộp bằng async single-flight per-key; key khác không bị global serialization. `ThumbnailCache` đăng ký singleton để memory tier được chia sẻ và mọi collection công khai vẫn immutable. PLAN 36 không triển khai metadata-first UI, lazy full-texture load, background queue, crash recovery hoặc App Shell.

## Lazy Loading từ PLAN 37

Smart Scan giờ là tầng metadata-first: scanner/hash, real `DdsMetadata` và manifest resolution được phát hành trong immutable `SmartTextureAsset`, nhưng model không chứa `InternalImage`. `SmartModScanRequest` cũng không còn thumbnail size vì scan không tạo thumbnail.

```text
Open/recover project → Smart Scan → metadata-only texture catalog
                                      ├─ LoadThumbnailAsync → IThumbnailCache
                                      └─ user selection → LoadSelectedTextureAsync
                                                           → IDdsPreviewService
                                                           → IImageImportService
                                                           → full InternalImage
```

`ITextureLazyLoadingService` là orchestration boundary cho hai bước sau metadata. Thumbnail reuse content-hash cache PLAN 36. Selected-texture load decode mip 0 qua DirectXTex preview boundary hiện có, đối chiếu toàn bộ observed DDS metadata với snapshot scan trước khi import pixels, rồi trả immutable full image. Service stateless và không giữ full-resolution cache; lifetime ảnh full thuộc caller/editor tương lai.

Thumbnail miss có thể transiently decode nguồn để tạo ảnh nhỏ theo pipeline PLAN 36, nhưng full-resolution editor image không được publish hoặc resident cho đến explicit selected API. PLAN 37 chưa có UI selection model, viewport prefetch, task queue, notification, crash recovery hay App Shell.

## Background Task Manager từ PLAN 38

`IBackgroundTaskManager` là queue trung tâm, UI-independent cho tám typed job kind: `Extract`, `Scan`, `Thumbnail`, `Resize`, `Convert`, `Build`, `Download`, `Ai`. Request cung cấp internal async operation nhận `IProgress<BackgroundTaskProgress>` và manager-owned cancellation token; operation thực tế vẫn phải gọi archive/DDS/image/AI boundary tương ứng.

```text
typed request → bounded Channel
                → N workers (configured cap)
                → Queued → Running → Succeeded | Failed | Cancelled
                            └─ validated progress snapshots
                                  └─ ordered per-task notifications
```

Manager là singleton hosted service. Queue có backpressure fail-fast, worker concurrency có hard cap, cancellation source riêng từng job được link với application shutdown. Cancel queued job publish terminal state ngay và operation không chạy; cancel running job truyền token cho operation. Exception ngoài dự kiến được cô lập thành safe diagnostic code nên worker tiếp tục xử lý job sau.

Snapshot/notification là immutable; invalid progress không thay thế last valid progress; completed history được giữ in-memory có giới hạn và evict oldest completion. PLAN 38 không persist task/delegate/result payload, không tự động chuyển toàn bộ workflow cũ vào queue, không triển khai download/AI backend, UI notification, session file hoặc crash recovery PLAN 39.

## Temp Cleanup & Crash Recovery từ PLAN 39

Mỗi secure workspace giữ exclusive `.workspace.lock`, đồng thời file này là versioned session marker chứa random session ID, process ID, creation timestamp và retained state. Session ID/PID/timestamp hỗ trợ diagnostics; bằng chứng workspace còn active duy nhất là khả năng giữ exclusive file handle, không phải PID lookup hay tuổi timestamp.

`SecureWorkspaceService` đồng thời triển khai `IWorkspaceCrashRecoveryService` và chạy discovery khi host start. Discovery chỉ enumerate direct lowercase-hex child dưới managed `Temp/Workspaces`, kiểm tra containment/reparse/tree/marker rồi atomic-publish immutable inventory:

- `ActiveOrInaccessible`: lock đang được giữ hoặc không đủ bằng chứng an toàn; không action.
- `StaleRecoverable`: lock đã nhả, marker hợp lệ và đủ `Working/Extracted/BuildOutput`; cho recover hoặc explicit cleanup.
- `StaleCleanupOnly`: marker hợp lệ nhưng workspace chưa hoàn tất; chỉ explicit cleanup.
- `Unsafe`: marker/path/tree không hợp lệ; không recover/cleanup tự động.

Startup không xóa candidate. `RecoverAsync` re-inspect rồi reuse exact `TryOpenExistingAsync`; `CleanupAsync` chỉ nhận workspace ID, resolve lại direct managed root, acquire lock, kiểm tra marker/reparse lần nữa và giữ lock trong lúc xóa. Thành công loại candidate khỏi offer. `.audproj`, pristine template, archive, project root và workspace khác không bị sửa. PLAN 39 không có UI prompt, content repair, project migration hay Design System.

## Design System từ PLAN 40

WinUI resources dùng bốn dictionary merge theo dependency order: primitives → semantic theme tokens → component tokens → reusable component styles. `Default`, `Light` và `HighContrast` publish cùng semantic color-key contract; component styles không chứa raw hex và vì vậy theme switching không cần fork template.

Foundation hiện có gồm shared acrylic/fallback gaming panel, standard và accent-gradient rounded cards, primary/secondary depth buttons với hover/pressed/disabled states, status badge container cùng section/body typography. Button template giữ `Button` semantics và system focus visual; motion chỉ dùng opacity/transform ngắn. Window Mica hiện hữu tiếp tục là top-level backdrop, còn acrylic chỉ là shared panel brush để giới hạn overdraw.

Chi tiết token/component usage nằm trong `docs/DESIGN_SYSTEM.md`. PLAN 40 không tạo navigation, page layout, dashboard/sidebar, texture grid/editor, view model hay workflow binding; toàn bộ App Shell thuộc PLAN 41.

## App Shell từ PLAN 41

- `MainWindow` chỉ sở hữu window/title-bar wiring và host `MainPage` được DI cấp; không resolve route bằng service locator và không chạy business operation.
- `AppShellViewModel` là presentation state in-memory. Route dùng enum `AppRoute` cùng một catalog code-owned; route label không được dùng làm filesystem, project, template hoặc entitlement identity.
- `NavigationView` cung cấp sidebar thích ứng và native keyboard/selection semantics. Content PLAN 41 chỉ là placeholder; các page Home, Project Workspace, Texture Grid và Crop/Resize được mở ở PLAN tương ứng.
- Top bar chỉ hiển thị trạng thái trung tính khi auth/cloud chưa tồn tại. Credits và connection trong client không phải authority; shell không chứa token, secret, executable path, process argument hoặc trusted hash.
- Shell không tạo background queue, không poll process và không tự cleanup/recover workspace. Khi PLAN sau cần trạng thái task/recovery, orchestration phải reuse contract PLAN 38/39.

## Home: Game → Mod First từ PLAN 42

- `HomeViewModel` chỉ nhận `IGameCatalog`, `IModCatalog`, `IProjectCreationService`, `IBackgroundTaskManager` và application project session qua DI. UI option chỉ chứa typed Game/Mod ID cùng display metadata cần thiết; không expose archive template, hash, executable path hoặc region selector.
- Chọn Game luôn xóa Mod selection cũ rồi query `IModCatalog.GetMods(GameId)`. Create chỉ được bật khi selected Mod thuộc snapshot tương thích hiện tại và project name hợp lệ. Production Mod Catalog rỗng tiếp tục là empty state an toàn, không được thay bằng fake built-in mapping.
- Create chạy như một job được PLAN 38 quản lý; operation gọi duy nhất `IProjectCreationService`, map progress có cấu trúc và không đưa raw diagnostic/exception ra UI. Cancel đi qua task ID typed của manager.
- `ApplicationProjectSession` là owner cấp ứng dụng cho exact `AuditionProject` cùng retained `IProjectArchiveWorkspace` do workflow trả về. Nó không parse/save project và không tạo source of truth song song; lease được dispose khi host shutdown hoặc khi session được thay thế.
- Home không có file/archive picker, không đọc `.audproj`, không chạy ACV/DDS/image trực tiếp và không tự chọn template. Project Workspace thuộc PLAN 43.

## Project Workspace UI từ PLAN 43

- Route Projects resolve `ProjectWorkspacePage` qua DI. ViewModel lấy exact project/workspace lease từ application session; không mở `.audproj`, không tự dựng workspace và không dispose lease khi đổi route.
- Texture inventory được làm mới qua một job `Scan` của PLAN 38 gọi `ISmartModScanService`. Kết quả immutable được map thành presentation model không có absolute path/tool path/trusted hash; texture state luôn đến từ `ITextureStateMachine`.
- Left pane cung cấp folder tree, search in-memory theo display name/raw filename/relative path và mapping filter `All/ManifestMapped/Unmapped`. Center/right/status chỉ bind selected presentation record; không đọc lại DDS header hoặc filesystem.
- Preview surface không eager-decode pixels. Editor/replace/reset buttons vẫn disabled cho đến PLAN/workflow được phê duyệt; UI không tạo mutation path giả. Texture Grid/status facets thuộc PLAN 44 và interactive canvas thuộc PLAN 45.
- Activation idempotent theo ProjectId khi immutable scan đã được publish. No active project, loading, cancelled và safe failure đều là explicit state; raw exception/diagnostic không hiện cho người dùng.

## Texture Grid + Search từ PLAN 44

- `ProjectWorkspaceViewModel` giữ một immutable metadata snapshot cho tree và grid; mọi search/facet chạy trong một pipeline xác định, không enumerate filesystem hoặc đọc lại DDS.
- Presentation record chỉ có relative identity và metadata cần hiển thị. Mapping nội bộ từ relative path sang `SmartTextureAsset` giữ source hash ngoài UI contract và chỉ phục vụ request lazy thumbnail.
- Grid dùng native `GridView`, textual status và card semantics. Thumbnail chỉ được yêu cầu khi container được hiện thực hóa, qua job `Thumbnail` của PLAN 38 gọi `ITextureLazyLoadingService` của PLAN 37 với cạnh tối đa 192 px.
- Size facet dùng cạnh lớn nhất: Small ≤ 512 px, Medium 513–2048 px, Large > 2048 px. Category đến từ manifest và fallback `uncategorized`; alpha đến từ DDS metadata snapshot.
- Chuyển RGBA8 straight thumbnail sang BGRA8 premultiplied là projection giới hạn trong UI. Grid không gọi full-texture API, không cache full-resolution pixels và không mutation texture/project/archive.

## Crop/Resize Canvas UI từ PLAN 45

- Route `ImageEditor` resolve `ImageEditorPage`/`ImageEditorViewModel` qua DI. Presentation boundary `IWorkspaceTextureSelection` chia sẻ exact selected texture với workspace mà không công bố absolute path, hash hoặc tool metadata.
- Khi route editor được mở, `ProjectWorkspaceViewModel` enqueue job `Convert` qua PLAN 38 rồi gọi explicit `ITextureLazyLoadingService.LoadSelectedTextureAsync` của PLAN 37. Editor giữ `InternalImage` trong lifetime route và release cả image/bitmap khi rời route; không có full-resolution service cache mới.
- `ImageEditorViewModel` dùng `IImageTransformService` PLAN 22 cho immutable zoom/pan/crop state, pixel crop và viewport projection. Sáu lựa chọn UI map explicit tới `ManualCrop`, `Fit`, `Fill`, `Stretch`, `CanvasResize`, `TransparentPadding` của PLAN 21 và tạo typed `ImageResizeRequest` với exact DDS target dimensions.
- Canvas chỉ là render projection: target frame giữ exact target aspect/dimension label; pointer drag cập nhật pan, wheel/slider cập nhật zoom, crop percentage đi qua normalized crop validation. RGBA8 straight → BGRA8 premultiplied adapter ghi theo từng row để tránh thêm một full-frame conversion buffer.
- PLAN 45 không gọi `IImageResizeService`, không Apply/encode DDS, không ghi edit history/project/extracted texture/archive và không triển khai compare PLAN 46. Các mode là preview + validated request intent cho workflow sau.
## PLAN 46 — Before/After Compare

Compare là presentation layer read-only của Image Editor. `BeforeImage` reuse đúng immutable
`InternalImage` được load từ project working copy khi editor session bắt đầu; nó không đọc pristine
template, thumbnail hoặc cache. `AfterImage` là kết quả preview không phá hủy do
`IImageResizeService` tạo từ current editor request. Preview chạy ngoài UI thread, có cancellation và
generation identity để kết quả cũ không publish đè edit mới. Compare không gọi persistence, DDS
encoder, archive service, texture state machine hoặc edit history.

Side-by-side, slider và toggle dùng chung một baseline bitmap và một After bitmap từ
`InternalImageBitmapAdapter`; các mode không tạo bitmap riêng. Slider chỉ cập nhật UI clip. Before và
After có edit geometry độc lập nhưng dùng cùng `CompareZoom`/`ComparePan`, được chiếu qua
`IImageTransformService`. Checkerboard chỉ là các surface dùng semantic theme brush và không đi vào
pixel/output. Route unload hủy preview đang chạy và bỏ toàn bộ `InternalImage`/`WriteableBitmap`
reference của editor.

Với fixture 6000×1801 RGBA8, một packed pixel buffer là 43.224.000 byte (xấp xỉ 41,22 MiB).
Trạng thái ổn định xấu nhất gồm Before + After `InternalImage` và hai `WriteableBitmap`, xấp xỉ
164,9 MiB, chưa tính buffer native/transient trong lúc resize. Vì vậy compare không lưu frame history,
không clone baseline và thay/release After presentation resource ngay khi generation mới được publish.

## PLAN 47 — Apply Texture UX

`ITextureApplyService` là orchestration boundary duy nhất cho Apply. UI chỉ gửi immutable project,
exact retained workspace, normalized texture identity và typed `ImageResizeRequest`; operation chạy qua
Background Task Manager và hỗ trợ progress/cancel. Pipeline bắt buộc là target DDS metadata/profile
validation → `IImageResizeService` → `IDdsMatchOriginalService` vào candidate cùng secure workspace →
`IDdsValidationService` độc lập → atomic replace extracted target → project history/asset snapshots →
`TextureState.Modified` → content-hash thumbnail regeneration → atomic `.audproj` save.

Before/After history asset là durable DDS snapshot dưới `BuildOutput/EditAssets`, định danh bằng role,
revision và prefix SHA-256; target filename/path không đổi. Candidate/backup nằm trong randomized
`BuildOutput/ApplyTransactions`. Save/thumbnail/state failure trước commit phục hồi target từ backup và
xóa asset snapshots vừa tạo. Global/pristine archive không được resolve hoặc mutate. Sau success, app
session nhận immutable project mới và workspace inventory được rescan; editor reload working DDS để
session baseline kế tiếp phản ánh byte thực sau encode, không reuse preview trước nén.

## Project Validator từ PLAN 48

- `IProjectValidator` trong Core định nghĩa kết quả có cấu trúc `Error/Warning/Info`; implementation ở
  Projects chỉ orchestration các boundary metadata cache, archive scanner, DDS metadata reader và tool
  integrity probe. Core không phụ thuộc Archives/Projects implementation hoặc UI.
- Metadata cache có API load immutable structural baseline. Baseline là metadata của exact extracted DDS
  lúc tạo project, không phải thumbnail/cache pixels và không được dùng thay bytes hiện tại; validator luôn
  quét và đọc lại working copy hiện tại trước khi cho phép build.
- Workspace/archive/folder/path được kiểm tra trước; texture identity tiếp tục là normalized relative path.
  Mỗi expected DDS được phân biệt missing, malformed, wrong dimensions và wrong format; DDS ngoài baseline
  được báo wrong filename. Thiếu/hỏng baseline, pending edit và integrity failure đều chặn build.
- Validator là read-only và cancellable: không save project, không tái tạo cache, không sửa DDS/archive,
  không chạy pack và không tự xử lý lỗi. Build orchestration PLAN 49 phải gọi boundary này và chỉ tiếp tục
  khi `CanBuild` là true.
