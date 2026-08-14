# Thuật ngữ giao diện tiếng Việt

Trạng thái: `APPROVED_FOR_PLAN_101_UX`

Ngôn ngữ mặc định của Audition AI Mod Studio V1 là `vi-VN`. Tên thương hiệu và định danh trong source code không dịch. Chuỗi giao diện tĩnh cốt lõi dùng tài nguyên WinUI tại `Strings/vi-VN/Resources.resw`; chuỗi động phải tuân theo từ điển này.

## Thuật ngữ sản phẩm

| Khái niệm | Cách viết thống nhất |
|---|---|
| Home | Trang chủ |
| Project / Projects | Dự án |
| Project Workspace | Không gian dự án |
| Create project | Tạo dự án |
| Open project | Mở dự án |
| Recent projects | Dự án gần đây |
| Image Editor | Trình chỉnh sửa ảnh |
| Texture Grid | Thư viện Texture |
| Inspector | Thuộc tính |
| Metadata | Thông tin Texture |
| No project | Chưa có dự án |
| No active project | Chưa mở dự án |
| No texture selected | Chưa chọn Texture |
| Settings | Cài đặt |
| Account | Tài khoản |
| AI Studio | AI Studio |
| Build & Export | Build & Xuất file |

## Quy trình dự án

| Trạng thái | Cách viết thống nhất |
|---|---|
| Archive | Tệp nguồn khi chỉ archive đầu vào; giữ `archive` trong mô tả kỹ thuật ngắn |
| Extracted | Đã giải nén |
| Scanned | Đã quét |
| Editing | Đang chỉnh sửa |
| Applied | Đã áp dụng |
| Validated | Đã kiểm tra |
| Built | Đã Build |
| Exported | Đã xuất |

## Trình chỉnh sửa ảnh

| Khái niệm | Cách viết thống nhất |
|---|---|
| Canvas | Khung chỉnh sửa |
| Crop / Resize | Cắt ảnh / Đổi kích thước |
| Crop & Resize | Cắt & đổi kích thước |
| Undo / Redo | Hoàn tác / Làm lại |
| Reset | Đặt lại |
| Zoom / Fit | Thu phóng / Vừa khung |
| Before / After | Trước / Sau |
| Side-by-side / Split | Song song / Chia đôi |
| Apply texture | Áp dụng Texture |
| Crop frame | Vùng cắt |
| Width / Height | Chiều rộng / Chiều cao |
| Left / Top | Trái / Trên |

## AI và tài khoản

| Khái niệm | Cách viết thống nhất |
|---|---|
| Prompt | Mô tả |
| Negative prompt | Nội dung cần tránh |
| Prompt preset | Mẫu gợi ý |
| Generate preview | Tạo bản xem trước |
| Job history | Lịch sử tạo |
| Advanced settings | Cài đặt nâng cao |
| Source-aligned mask | Mask theo ảnh nguồn |
| Approve and Apply | Dùng kết quả này |
| Credits / Balance | Credits / Số dư Credits |
| Recent activity | Hoạt động gần đây |
| Not connected | Chưa kết nối |

## Từ tiếng Anh được phép

- `ALLOWED_BRAND`: Audition AI Mod Studio, Audition AI, MOD STUDIO.
- `ALLOWED_TECHNICAL_TERM`: AI, DDS, BC3, DXT5, Alpha, Credits, Portable, x64, Build, Texture, archive, SHA-256, Windows, RAM, SSD, Mask, Inpaint, Outpaint, Upscale.
- `NOT_USER_VISIBLE`: tên lớp, enum, property, mã chẩn đoán, logging template, tên package, đường dẫn và định danh giao thức nội bộ.
- Mọi chuỗi hiển thị tiếng Anh khác là `NEEDS_TRANSLATION` và làm language gate thất bại.

## Quy tắc nội dung và bố cục

- Viết tự nhiên, ngắn, chủ động; không đưa câu chữ backend hoặc stack trace ra giao diện.
- Nút giữ chiều cao tối thiểu 44 px; nhãn dài được co giãn hoặc xuống dòng, không cắt dấu tiếng Việt.
- Dùng font hệ thống WinUI hỗ trợ đầy đủ dấu tiếng Việt. Kiểm tra riêng ở 1920×1080 và 1366×768 trước bàn giao.
