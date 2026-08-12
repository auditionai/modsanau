# ADR-0003: Chiến lược obfuscation binary

- Trạng thái: Accepted evaluation; **không adopt production trong PLAN 78**
- Ngày: 2026-08-12
- Chủ sở hữu: Release Engineering, Desktop Architecture, Security, QA

## Bối cảnh

Client .NET có thể bị decompile/patch. Obfuscation chỉ tăng chi phí reverse engineering; nó không thể bảo vệ AI key,
service-role key, payment state, credit, entitlement, update authority hay template plaintext đã cần xuất hiện trên máy
authorized user. Các authority đó tiếp tục ở server/trust boundary hiện hữu.

Ứng dụng hiện dùng .NET 10, WinUI 3/XAML, Generic Host/DI, JSON source/runtime serialization, P/Invoke Windows,
SkiaSharp, DirectXTex process boundary và nhiều assembly app-owned. Transform tên/control flow/resource không được giả
định tương thích chỉ vì một console sample chạy.

## Đánh giá candidate

Candidate pilot ưu tiên là **PreEmptive Protection Dotfuscator Professional 7.2.2**. Tài liệu official hiện tại:

- Supported Frameworks liệt kê .NET 10 và lưu ý support phụ thuộc app type/build/protection settings:
  `https://support.preemptive.com/hc/en-us/articles/48641068598033-Supported-Frameworks-and-Development-Tools`.
- Renaming, control flow, string encryption, MSBuild integration và map file có tài liệu versioned.
- Vendor yêu cầu map/report là private build artifact và map phải khớp exact release để decode stack trace.
- Dotfuscator 7 không còn tự Authenticode-sign output; signing phải là stage sau obfuscation.

Không chọn Community edition cho CI production vì command-line/protection capability bị giới hạn. Không adopt
Obfuscar/Eazfuscator hay vendor khác trong PLAN này vì chưa có cùng bộ evidence .NET 10 + WinUI 3 + protected CI/license.
Việc ưu tiên candidate không phải purchase/vendor lock; license, support SLA, data/telemetry và supply-chain review vẫn mở.

## Quyết định

Trạng thái PLAN 78 là **EVALUATED / NOT ADOPTED / PRODUCTION NOT VERIFIED**. Không thêm NuGet vendor, license, target
MSBuild hay config giả. Debug/dev và Release hiện tại đều giữ bytes không-obfuscated. Không được báo Release “protected”
cho đến khi pilot gate dưới đây PASS trên approved isolated build agent.

Thứ tự release bắt buộc nếu tương lai adopt:

```text
exact source/version restore
  → Release publish (unsigned)
  → obfuscate app-owned managed assemblies
  → protected functional/performance/AV tests
  → archive private map/report by version + commit + config/tool digest
  → Authenticode sign + RFC 3161 timestamp
  → PLAN 76 signature verify
  → PLAN 77 update manifest binds final signed bytes/hash
  → public artifact upload
```

Không transform sau signing. Third-party/native binaries không được rewrite. Public release artifact không chứa map,
unprotected duplicate, vendor license hoặc report có original symbol inventory.

## Protection profile theo giai đoạn

1. **Pilot 1 — renaming only:** process app-owned assemblies như một graph. Exclude/preserve WinUI Page/ViewModel/code-behind
   và XAML/manifest referenced names; JSON DTO/member names; DI/reflection-discovered types; P/Invoke/COM structs/exports;
   public plugin/update/archive contracts; assembly/resource names mà runtime resolve bằng string.
2. **Pilot 2 — control flow low/medium:** chỉ selected pure managed implementation methods. Đo startup, image 6000×1801,
   DDS encode, archive scan/build và AI payload path. Không áp dụng vào async/native interop/hot loops trước evidence.
3. **Pilot 3 — selected string encryption:** chỉ non-localizable implementation literals sau profiler. Không coi đây là
   secret storage; không encrypt resource keys, XAML bindings, diagnostics/protocol IDs hoặc interop names.
4. **Resource protection:** disabled cho tới khi XAML PRI/assets/themes/localization/package/unpackaged matrix PASS.
5. **Anti-tamper/checks:** disabled. Chỉ mở bằng PLAN/approval sau Defender/SmartScreen/multi-vendor AV, WinUI startup,
   Authenticode, updater, crash/recovery và false-positive matrix. Không anti-debug/injection/malware-like response.

## Pilot acceptance gate

- Debug build chứng minh không chạy vendor tool và symbols/debugging không bị transform.
- Release protected smoke: startup/XAML navigation, auth/session, project create/open/edit/Apply/build/export.
- Toàn bộ test Debug/Release, App/Gateway/XAML, real ACV Tool 5 và DirectXTex gates PASS trên protected output phù hợp.
- Reflection/DI/JSON/PInvoke/XAML/resource inventory có explicit keep rules và negative regression tests.
- So sánh startup, memory, editor, 6000×1801 encode và build latency với threshold được product/QA phê duyệt.
- Defender/SmartScreen và ít nhất một approved independent AV/reputation workflow không có unexplained detection.
- Crash stack obfuscated được decode bằng exact private map; map access/audit/retention/delete/incident runbook PASS.
- Decompile review chứng minh transform thực sự áp dụng; không chỉ tool exit code.
- Obfuscator/tool/license provenance, exact version/hash, license redistribution và offline/CI activation được review.
- Output cuối được sign/timestamp/verify lại và update manifest bind post-sign SHA-256.

## Mapping, versioning và incident

Mỗi map bind exact semantic version, source commit, RID, tool version/hash và config digest. Map/report đi vào private
artifact store với least privilege, retention dài bằng support lifetime; không vào repository, public artifact, logs hay
customer diagnostic export. Dùng sai map phải fail decode thay vì tạo stack trace sai.

Nếu map leak: revoke access, preserve audit, rotate artifact-store credential, đánh giá IP exposure; không cần rotate
economic/server keys vì chúng không được phép nằm trong map/client. Nếu obfuscator gây regression/AV false positive,
rollback bằng rebuild từ exact source không bật obfuscation rồi sign như một release mới; không sửa binary đã ký.

## Hệ quả và non-scope

- Positive: có candidate và gate thực tế, không tạo false production claim hoặc dependency/license chưa kiểm chứng.
- Negative: binary hiện vẫn dễ decompile; đây là residual IP risk được chấp nhận tạm thời.
- Không thay manifest privilege, updater installer, app/server authority, project/archive output hoặc game boundary.
- Native AOT thuộc PLAN 79; moderate client integrity thuộc PLAN 80. Aggressive anti-debug/tamper không được phê duyệt.
