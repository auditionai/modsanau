# ADR-0004: Đánh giá Native AOT và native core

- Trạng thái: Accepted evaluation; **không adopt production trong PLAN 79**
- Ngày: 2026-08-12
- Chủ sở hữu: Desktop Architecture, Security, Release Engineering, QA

## Bối cảnh và bằng chứng

Native AOT là lựa chọn triển khai self-contained, platform-specific có thể cải thiện startup/memory trong một số workload.
Nó không làm client thành “uncrackable DRM”, không che được plaintext cần dùng trên máy authorized user và không thay server
authority cho AI secret, credit, entitlement, payment hay signing key.

Ngày 2026-08-12, repository đã chạy publish thực tế, không suppress analyzer:

```powershell
dotnet publish src/AuditionModStudio.App/AuditionModStudio.App.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishAot=true `
  -p:WindowsPackageType=None -p:EnableMsixTooling=false -o <isolated-temp>
```

Kết quả: **FAIL, exit 1** trước native link. `IL2026` và `IL3050` được nâng thành error theo policy hiện hữu tại
`WindowsCredentialSessionStore`, settings, project/workspace/preset persistence, cloud/Supabase HTTP JSON và update manifest.
Các call site dùng reflection-based `System.Text.Json`, non-generic `JsonStringEnumConverter` hoặc overload cần dynamic code.
Analyzer không được tắt và lỗi không được whitelist để tạo PASS giả.

Tài liệu Microsoft được dùng làm baseline:

- Native AOT có giới hạn dynamic assembly loading, runtime code generation, built-in COM trên Windows và bắt buộc trimming:
  <https://learn.microsoft.com/dotnet/core/deploying/native-aot/>.
- Compatibility phải được chứng minh bằng publish analyzer và runtime tests; third-party library không được giả định tương thích:
  <https://learn.microsoft.com/dotnet/maui/deployment/nativeaot>.

## Ma trận tương thích

| Thành phần | Bằng chứng PLAN 79 | Kết luận |
|---|---|---|
| WinUI 3/XAML | Publish chưa đi qua serializer analyzer tới native link/XAML startup | Chưa xác minh runtime; không adopt |
| DirectXTex | `texconv.exe` chạy sau `IDdsService` bằng absolute external-process boundary | Không bị compile AOT trực tiếp; real-tool smoke vẫn bắt buộc |
| Supabase/backend client | Custom `HttpClient`; `JsonContent`/`ReadFromJsonAsync` phát `IL2026/IL3050` | Cần source-generated `JsonSerializerContext` |
| Serializer/persistence | Nhiều typed/generic reflection overload và enum converter động phát lỗi | Blocker đã xác nhận |
| SkiaSharp/native interop | Managed/native package restore được, nhưng publish dừng trước link/runtime image test | Chưa xác minh runtime |
| Windows P/Invoke/WinTrust | Static interop hiện hữu; Native AOT Windows không có built-in COM | Cần smoke trên supported Windows, không suy diễn |
| Plugin | Repository không có runtime plugin loader; Native AOT không hỗ trợ dynamic assembly loading | Future plugin phải static/rooted hoặc giữ JIT host |

## Quyết định

PLAN 79 là **EVALUATED / NOT ADOPTED / PRODUCTION NOT VERIFIED**. Không thêm `PublishAot` vào project/publish profile,
không ship output thử nghiệm và không chuyển thư viện sang C++/Rust/native chỉ để làm khó decompile. Bản Release tiếp tục mô hình
hiện hữu. Script `Invoke-NativeAotEvaluation.ps1` tái lập exact Release probe ở output tuyệt đối ngoài repository và cấm Debug.

Không chọn “selected native core” lúc này: các phép hash/signature/AES/P-256 đã dùng implementation platform/runtime được review;
archive/DDS/image còn native/process dependency; thêm ABI, memory-safety và signing surface mới chưa có lợi ích benchmark chứng minh.

## Điều kiện xem xét lại

1. Chuyển toàn bộ persistence/auth/update/client JSON sang source-generated contexts và generic enum converters; AOT analyzer zero.
2. Publish exact signed x64 candidate thành công, lưu SDK/package lock/provenance; không suppress `IL2026`/`IL3050` diện rộng.
3. Smoke startup/XAML/navigation, DI, auth refresh/session migration, project create/open/save, image 6000×1801, DDS/SkiaSharp,
   real DirectXTex, ACV extract/pack/build/export và update verification trên supported clean Windows.
4. Đo cold/warm startup, working set, package size và các hot-path latency so với JIT Release bằng threshold được duyệt.
5. Kiểm tra crash dump/symbol, signing, updater, AV/SmartScreen, accessibility và rollback. Plugin policy phải được chốt trước.
6. Chỉ adopt bằng PLAN riêng sau khi lợi ích đo được lớn hơn chi phí compatibility/operations; AOT vẫn chỉ là hardening.

## Hệ quả và residual risk

- Không có false claim rằng binary production đã AOT hoặc bảo vệ secret/IP tuyệt đối.
- Managed Release vẫn dễ decompile; server authority, Authenticode, signed update và PLAN 80 integrity mới là control phù hợp.
- Probe hiện chỉ chứng minh blocker sớm; chưa phải bằng chứng WinUI/SkiaSharp/native runtime không bao giờ tương thích.
- Output temp của probe không phải release artifact và phải được operator xóa theo local build hygiene.
