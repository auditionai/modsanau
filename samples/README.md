# Real sample fixtures

Các binary/game asset thực tế không được commit. PLAN 05 đăng ký các vị trí local sau:

- repository root: `acv.exe`;
- repository root: `015.ab`;
- `samples/private/tn_coby_logo.dds`.

`samples/private/` đã bị `.gitignore` loại trừ. Test có khả năng thay đổi dữ liệu phải copy fixture vào isolated per-test workspace trước, không được ghi trực tiếp vào các file trên.

Metadata kỳ vọng của `tn_coby_logo.dds`: 6000×1801, DXT5/BC3, 1 mip level. Metadata này là fixture expectation; việc decode/encode DDS thuộc PLAN sau.
