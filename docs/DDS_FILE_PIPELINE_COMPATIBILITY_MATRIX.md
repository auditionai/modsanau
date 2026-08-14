# Ma trận tương thích DDS file-pipeline

Tài liệu này ghi lại bằng chứng thực thi của PLAN 98. Ma trận không suy diễn hỗ trợ từ việc đọc được header: một ô chỉ mang trạng thái `SUPPORTED` khi đúng stage tương ứng đã chạy thành công. `UNSUPPORTED` là biên sản phẩm có chủ ý và có failure typed; `INVALID` là input sai phải fail closed; `NOT VERIFIED` là chưa có bằng chứng đủ thẩm quyền.

Nguồn sự thật có thể thực thi nằm tại `DdsCompatibilityMatrixEvidence` và các integration test PLAN 98. Test so sánh nguyên văn bảng dưới đây để tài liệu không thể drift khỏi định nghĩa. DirectXTex được dùng qua boundary hiện hữu với exact SHA-256 `DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06`.

## Corpus và phạm vi bằng chứng

- Level A dùng synthetic DDS có kiểm soát để chứng minh pipeline DDS độc lập cho bốn format production hỗ trợ: BC1, BC3, RGBA8 và BGRA8; bao gồm Legacy/DX10, linear/sRGB, mip chain, alpha và kích thước không power-of-two.
- Level B đọc private real fixture từ working copy của `015.ab`: 52 DDS, gồm BC1=2, BC3=4, BGRA8=23, RGBA8=23; 49 file có 1 mip, 2 file có 8 mip, 1 file có 9 mip; toàn bộ là Legacy Texture2D đơn. Test ghi một record deterministic cho từng file với path tương đối, kích thước, format, mip, alpha, header và resource.
- Archive roundtrip chỉ được đánh dấu `SUPPORTED` cho slot `texture/hud/pointer.dds` đã có full evidence PLAN 97: 164×128, BC3/DXT5, 1 mip, interpolated alpha, Legacy; 319 file ngoài target byte-identical. Format hoặc sample khác không được thừa hưởng kết luận archive này.
- Production Mod Catalog hiện rỗng có chủ ý vì repository chưa có metadata Mod Type có thẩm quyền. Corpus của Mod Type tương lai vì vậy giữ `NOT VERIFIED`, không đoán nhãn từ filename/path và không ép format vào một slot không tương thích.
- BC2/BC4/BC5/BC6H/BC7 và legacy bitmask khác có thể được metadata reader nhận diện, nhưng editor chưa có encode/Match Original end-to-end. Texture1D, Texture3D, array và cubemap nằm ngoài model single-subresource hiện tại.

## Ma trận chuẩn

<!-- MATRIX:START -->
| Case ID | Source | Scope | Format | Header | Dimensions | Mips | Alpha | Resource | Metadata | Preview + Import | Encode | Match + Validate | Re-decode | Archive | Overall | Reason |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `invalid-malformed-corpus` | Synthetic | Parser/security boundary | Various | Legacy/DX10 | Invalid/oversized | Invalid | Unknown | Invalid | INVALID | INVALID | INVALID | INVALID | INVALID | INVALID | INVALID | Malformed headers, payloads and arithmetic profiles must fail closed |
| `not-verified-future-mod-types` | Private real | Future authorized Mod Types | Unknown | Unknown | Unknown | Unknown | Unknown | Unknown | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | Production Mod Catalog currently has no authoritative built-in Mod Type corpus |
| `real-015-bc1-legacy` | Private real | Archive 015 representative | BC1/DXT1 | Legacy | 4000x4000 (2 samples) | 1 | PossibleOneBit | Texture2D single | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | NOT VERIFIED | NOT VERIFIED | DDS pipeline proven; no manifest-scoped archive roundtrip for this format |
| `real-015-bc3-legacy` | Private real | Archive 015 representatives | BC3/DXT5 | Legacy | 200x200; 256x256; 6000x1801 | 1; 8; 9 | Interpolated | Texture2D single | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | NOT VERIFIED | NOT VERIFIED | DDS pipeline proven; archive support remains slot-specific |
| `real-015-bgra8-legacy` | Private real | Archive 015 representatives | BGRA8 | Legacy | 64x64; 128x128; 512x256 | 1 | Channel | Texture2D single | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | NOT VERIFIED | NOT VERIFIED | DDS pipeline proven; no manifest-scoped archive roundtrip for this format |
| `real-015-rgba8-legacy` | Private real | Archive 015 representatives | RGBA8 | Legacy | 64x40; 64x64; 256x256; 512x512 | 1 | Channel | Texture2D single | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | NOT VERIFIED | NOT VERIFIED | DDS pipeline proven; no manifest-scoped archive roundtrip for this format |
| `real-plan97-pointer-bc3-legacy` | Private real | PLAN 97 pointer slot | BC3/DXT5 | Legacy | 164x128 | 1 | Interpolated | Texture2D single | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | Full Apply, build, pack, export and re-extract evidence |
| `synthetic-dx10-bc1-srgb` | Synthetic | DDS-only | BC1 | DX10 sRGB | 7x5 | 3 | Binary | Texture2D single | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | NOT VERIFIED | NOT VERIFIED | Full DDS pipeline proven; no authorized archive slot evidence |
| `synthetic-dx10-bc3-linear` | Synthetic | DDS-only | BC3 | DX10 linear | 9x7 | 4 | Interpolated | Texture2D single | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | NOT VERIFIED | NOT VERIFIED | Full DDS pipeline proven; no authorized archive slot evidence |
| `synthetic-dx10-bgra8-linear` | Synthetic | DDS-only | BGRA8 | DX10 linear | 17x11 | 1 | Channel | Texture2D single | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | NOT VERIFIED | NOT VERIFIED | Full DDS pipeline proven; no authorized archive slot evidence |
| `synthetic-dx10-rgba8-srgb` | Synthetic | DDS-only | RGBA8 | DX10 sRGB | 13x9 | 4 | Channel | Texture2D single | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | SUPPORTED | NOT VERIFIED | NOT VERIFIED | Full DDS pipeline proven; no authorized archive slot evidence |
| `unsupported-format-bc2` | Synthetic | DDS-only boundary | BC2/DXT3 | Legacy/DX10 | 8x8 | 1 | Explicit | Texture2D single | SUPPORTED | NOT VERIFIED | UNSUPPORTED | UNSUPPORTED | NOT VERIFIED | UNSUPPORTED | UNSUPPORTED | Metadata recognition is not encode, Match Original or archive support |
| `unsupported-format-bc4` | Synthetic | DDS-only boundary | BC4 | Legacy/DX10 | 8x8 | 1 | None | Texture2D single | SUPPORTED | NOT VERIFIED | UNSUPPORTED | UNSUPPORTED | NOT VERIFIED | UNSUPPORTED | UNSUPPORTED | Metadata recognition is not encode, Match Original or archive support |
| `unsupported-format-bc5` | Synthetic | DDS-only boundary | BC5 | Legacy/DX10 | 8x8 | 1 | None | Texture2D single | SUPPORTED | NOT VERIFIED | UNSUPPORTED | UNSUPPORTED | NOT VERIFIED | UNSUPPORTED | UNSUPPORTED | Metadata recognition is not encode, Match Original or archive support |
| `unsupported-format-bc6h` | Synthetic | DDS-only boundary | BC6H | DX10 | 8x8 | 1 | None | Texture2D single | SUPPORTED | NOT VERIFIED | UNSUPPORTED | UNSUPPORTED | NOT VERIFIED | UNSUPPORTED | UNSUPPORTED | Metadata recognition is not encode, Match Original or archive support |
| `unsupported-format-bc7` | Synthetic | DDS-only boundary | BC7 | DX10 | 8x8 | 1 | Channel | Texture2D single | SUPPORTED | NOT VERIFIED | UNSUPPORTED | UNSUPPORTED | NOT VERIFIED | UNSUPPORTED | UNSUPPORTED | Metadata recognition is not encode, Match Original or archive support |
| `unsupported-legacy-bitmask` | Synthetic | DDS-only | Other uncompressed masks | Legacy | 8x8 | 1 | Varies | Texture2D single | SUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | Reader parses the header but preserves the unsupported format classification |
| `unsupported-resource-array` | Synthetic | DDS-only boundary | RGBA8 | DX10 | 8x8 | 1 | Channel | Texture2D array size 2 | SUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | Editor model only supports non-cube Texture2D with array size 1 |
| `unsupported-resource-cubemap` | Synthetic | DDS-only boundary | RGBA8 | DX10 | 8x8 | 1 | Channel | Texture2D cubemap | SUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | Editor model only supports non-cube Texture2D with array size 1 |
| `unsupported-resource-texture1d` | Synthetic | DDS-only boundary | RGBA8 | DX10 | 8x8 | 1 | Channel | Texture1D | SUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | Editor model only supports non-cube Texture2D with array size 1 |
| `unsupported-resource-texture3d` | Synthetic | DDS-only boundary | RGBA8 | DX10 | 8x8 | 1 | Channel | Texture3D | SUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | Editor model only supports non-cube Texture2D with array size 1 |
<!-- MATRIX:END -->

## Cách đọc kết quả

`Overall` là kết luận thận trọng nhất cho toàn chuỗi, không phải giá trị lớn nhất của các stage. Một case DDS-only vẫn là `NOT VERIFIED` nếu chưa có manifest/slot archive có thẩm quyền. Một format được reader nhận diện vẫn là `UNSUPPORTED` nếu encode hoặc Match Original chưa được product hỗ trợ. Bảng không khẳng định tương thích runtime/in-game; pipeline kết thúc ở DDS hoặc standalone `.ab`/`.acv` đã re-extract.

Private fixture và executable không được commit hay phân phối bởi tài liệu này. Nếu fixture/tool hợp lệ không có trên máy test, gate tương ứng phải báo skip rõ ràng; synthetic/parser gates vẫn deterministic và không được đổi thành PASS giả.
