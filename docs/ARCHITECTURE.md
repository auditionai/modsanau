# Kiến trúc Audition AI Mod Studio

## Recursive Asset Scanner (PLAN 11)

Luồng production là `Working Archive → Extract → extracted directory của IProjectArchiveWorkspace → Recursive Scan → ArchiveAssetCatalog`. Scanner không đọc cây `015\` ở repository và không chứa logic DDS decode/convert, UI hay pack archive.

Contract/model (`ArchiveAsset`, `TextureAsset`, `ArchiveAssetCatalog`, `IArchiveAssetScanner`) nằm trong Core. Implementation filesystem `ArchiveAssetScanner` nằm trong Projects và chỉ nhận project workspace đã được cấp. DDS được phân loại thành `TextureAsset`; PNG, SLK, RGM và extension khác vẫn là asset catalog hợp lệ.

Identity ổn định là normalized relative directory path cộng exact filename, được biểu diễn bởi `RelativePath`; không dùng filename đơn lẻ và không đổi case/name. Kết quả sort bằng `StringComparer.Ordinal`. Duplicate logic theo filesystem Windows được phát hiện bằng `StringComparer.OrdinalIgnoreCase` và không bị overwrite.

Scanner duyệt không theo reparse point, resolve lại từng relative path qua `IPathSecurity`, hash SHA-256 theo stream 64 KiB, hỗ trợ cancellation/progress và không giữ mutable global state. Policy lỗi là fail toàn scan để không phát hành catalog thiếu mà caller tưởng là đầy đủ.

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
