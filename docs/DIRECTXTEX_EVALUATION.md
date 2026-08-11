# Đánh giá DirectXTex — PLAN 14

## Phạm vi và phiên bản

PLAN 14 dùng `texconv.exe` như compatibility/evaluation harness, không phải production UX, preview service hay encoder service.

- Nguồn: Microsoft DirectXTex release `may2026`.
- Phiên bản file/tool: `2026.5.8.1`.
- Kiến trúc: x64.
- SHA-256 được pin trong code: `DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06`.
- Binary chỉ được tải vào thư mục tạm để test, không được commit hoặc tự động lấy từ `PATH`/working directory.

Nguồn kỹ thuật chính thức:

- <https://github.com/microsoft/DirectXTex/releases/tag/may2026>
- <https://github.com/microsoft/DirectXTex/wiki/Texconv>
- <https://github.com/microsoft/DirectXTex/wiki/Getting-Started>
- <https://github.com/microsoft/DirectXTex/blob/main/DirectXTex/DirectXTex.h>

## Command đã xác minh

Decode DDS thành PNG preview:

```text
texconv.exe -nologo -y -ft png -o <isolated-output> -- <input.dds>
```

Encode PNG thành BC3/DXT5, giữ exact dimensions, một mip và legacy header:

```text
texconv.exe -nologo -y -ft dds -f BC3_UNORM -w 6000 -h 1801 -m 1 -dx9 -o <isolated-output> -- <input.png>
```

Các kết luận command-line:

- `-w`/`-h` nhận kích thước cụ thể; không truyền `-pow2`, vì vậy NPOT không bị ép thành power-of-two.
- `-m <n>` ép số mip output. Evaluation đã xác minh `-m 4` ở 137×512 và `-m 1` ở 6000×1801.
- `-f BC3_UNORM` tạo semantic BC3; khi kết hợp `-dx9`, output là legacy header với FourCC `DXT5`.
- `-ft png` decode cả BC1, BC3, RGBA8 và BGRA8 thực tế trong archive mẫu.
- `--` kết thúc option parsing, còn mọi argument được truyền riêng bằng `ProcessStartInfo.ArgumentList`; không có shell command concatenation.

## Kết quả real evaluation

Evaluation dùng disposable extract từ working archive, không dùng cây `repository-root\015` làm production source:

- Metadata regression: 52/52 DDS parse thành công, 0 fail, 0 unknown.
- Decode representative: 4/4 thành công, phủ BC1, BC3, RGBA8 và BGRA8.
- Encode BC3 legacy 137×512, 4 mip: thành công.
- Encode BC3 legacy 6000×1801, 1 mip: thành công.
- Decode-back output 6000×1801: thành công và giữ đúng dimensions.
- Hash 52 DDS nguồn trước/sau: không đổi.
- Hash pristine archive trước/sau: không đổi.

Các con số format/dimension này chỉ là observation của archive mẫu hiện tại, không phải default toàn cục của Audition. Mỗi target về sau tiếp tục lấy `IDdsMetadataReader` làm source of truth.

## RGBA trong bộ nhớ và hướng native wrapper

`texconv` CLI không nhận một raw RGBA memory buffer có row pitch tùy ý. Với pipeline file prototype, RGBA phải được đóng gói vào PNG/WIC input trước khi gọi CLI. Production pipeline muốn tránh temp PNG nên dùng narrow native wrapper quanh public DirectXTex API:

1. `CoInitializeEx` trên worker thread.
2. Kiểm tra `width`, `height`, `width * height`, `rowPitch`, `slicePitch` bằng checked arithmetic; dùng `ComputePitch` khi phù hợp.
3. `ScratchImage.Initialize2D(DXGI_FORMAT_R8G8B8A8_UNORM, width, height, 1, 1)`.
4. Copy từng row từ RGBA input theo validated source/destination row pitch; không giả định buffer packed nếu contract cho phép stride.
5. Nếu cần mip: `GenerateMipMaps(..., requestedMipLevels, ...)` sau khi validate bằng `CalculateMipLevels`.
6. `Compress(..., DXGI_FORMAT_BC3_UNORM, ...)`.
7. `SaveToDDSFile(..., DDS_FLAGS_FORCE_DX9_LEGACY, ...)` khi target metadata yêu cầu legacy header; dùng policy khác nếu target là DX10.

Đây là API finding, chưa phải native wrapper implementation của PLAN 14. Production encoder vẫn thuộc PLAN 16.

## Quyết định kiến trúc

- Giữ `DirectXTexEvaluationHarness` và `texconv` làm oracle/prototype có kiểm soát cho compatibility tests.
- PLAN 15 đã triển khai `IDdsPreviewService` sau abstraction bằng controlled `texconv`, reuse nguyên trust boundary PLAN 14. Preview request không expose harness/tool path; service trả immutable PNG bytes trong memory và cleanup temp output. Narrow native wrapper chưa cần thiết cho acceptance hiện tại và không được tự mở rộng sang P/Invoke/C++.
- PLAN 16 mới quyết định/triển khai production `IDdsEncoder`; không tái sử dụng một global DXT5 default. `DdsTargetSettings` phải xuất phát từ metadata target ở PLAN 17.

## Bảo mật và giới hạn

- Harness pin exact filename/version/hash, copy-verify binary vào isolated workspace và chạy bằng absolute path.
- Input/output bị confine trong `ISecureWorkspace`; output chỉ nằm dưới `BuildOutput`.
- DDS được preflight qua `IDdsMetadataReader`; PNG được kiểm tra signature/IHDR trước native process.
- Giới hạn mặc định: input tối đa 512 MiB, 100 triệu pixel và dimension encode tối đa 16384; mọi phép nhân pixel dùng checked 64-bit arithmetic.
- Process có timeout, cancellation, process-tree termination, bounded stdout/stderr và structured failure.
- Evaluation chưa chứng minh mọi DDS format, cubemap, array, volume, color profile hay malformed payload trên thế giới.
- Pin SHA-256 là integrity policy cho prototype, không thay thế Authenticode verification, dependency provenance/SBOM và release packaging review của PLAN sau.

## Finding production preview từ PLAN 15

- `texconv -ft png` giữ base dimensions, alpha và channel order trên synthetic RGBA8/BGRA8/BC1/BC3; red RGBA/BGRA không bị swap thành blue, BC3 alpha không bị flatten.
- Production service xử lý 52/52 real DDS của disposable extract, gồm BC1, BC3, RGBA8 và BGRA8; 6000×1801 và 4000×4000 giữ nguyên dimensions. Đây vẫn chỉ là coverage của archive mẫu hiện tại.
- Full-resolution PNG là UI-safe transport chứ không phải color-management engine. Service không tự gamma-convert; legacy unknown color space vẫn `Unknown`, DXGI sRGB metadata vẫn nằm trong source metadata. PNG writer behavior của DirectXTex/WIC không được dùng để suy đoán legacy sRGB.
- Output directory per request loại collision khi hai DDS khác folder có cùng basename. Tool provisioning chấp nhận race an toàn khi một request đồng thời đã đặt đúng pinned binary trước.

## Finding production encode từ PLAN 16

- `DdsTargetSettings` map tập trung: BC1→`BC1_UNORM`, BC3→`BC3_UNORM`, RGBA8→`R8G8B8A8_UNORM`, BGRA8→`B8G8R8A8_UNORM`; suffix `_SRGB` chỉ được dùng khi caller explicit yêu cầu sRGB.
- `-dx9` ép legacy header nhưng DirectXTex ghi SRGB thành non-SRGB, nên production contract từ chối legacy+sRGB. `-dx10` được dùng explicit cho DX10 và metadata color space được reader xác minh.
- `-m <n>` giữ exact requested mip count; không dùng `-m 0` hoặc `-pow2`. Input/target dimension mismatch bị reject trước tool, không resize.
- BC1 binary alpha dùng ngưỡng `-at 0.5`; BC1 full alpha bị reject. BC3/RGBA8/BGRA8 synthetic transparent roundtrip giữ alpha; RGBA/BGRA solid red decode-back không swap channel.
- Internal RGBA8 được đóng gói thành valid PNG bridge trong randomized `Working` operation vì CLI không nhận raw pixel buffer. Bridge bị cleanup; DDS được metadata-verify trong isolated output rồi atomic promote. Native wrapper vẫn là optimization tương lai, không cần để đạt PLAN 16.
- Real disposable target profiles BC1, BC3, RGBA8 và BGRA8 encode thành công mà không replace DDS/archive. 6000×1801 legacy BC3/1 mip và 256×256 DX10 sRGB/9 mips cũng đã được xác minh.
