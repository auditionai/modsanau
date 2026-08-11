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

`SecureWorkspacePaths` phân tách working archive, extracted files và build output. Pristine template nằm ngoài model writable này, trong `SecureTemplateCache`; PLAN 03 không có API ghi đè pristine template. `ISecureWorkspace` giữ exclusive handle trên `.workspace.lock` đến khi dispose. Host dispose `SecureWorkspaceService`, nhờ đó các lease còn thuộc process được cleanup trong graceful shutdown.

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
