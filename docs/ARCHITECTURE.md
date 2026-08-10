# Kiến trúc Audition AI Mod Studio

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
