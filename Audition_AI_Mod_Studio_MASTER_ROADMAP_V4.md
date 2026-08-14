# AUDITION AI MOD STUDIO — MASTER ROADMAP V4

## Phạm vi và tài liệu kế thừa

Roadmap V4 kế thừa toàn bộ kiến trúc, security boundary và lịch sử PLAN 01–100 trong
`Audition_AI_Mod_Studio_MASTER_ROADMAP_V3.md`. Nếu có xung đột về thứ tự productization/release thì roadmap V4 thắng.
`PRODUCT DIRECTION LOCK — FILE-ONLY ARCHIVE EDITOR` tiếp tục là ràng buộc có thẩm quyền cao nhất.

Phân phối V1 chính là **portable ZIP**. Kiến trúc MSIX của PLAN 95 được giữ lại để tham chiếu và cho kênh tương lai,
nhưng **không phải kênh phân phối V1 chính**. Runtime tiếp tục `asInvoker`; không yêu cầu Administrator.

## PRODUCTIZATION V4

### PLAN 101 — Visual Product Acceptance

- UX/Product Clarity.
- Portable ZIP Packaging.
- Bằng chứng phải đến từ ứng dụng WinUI chạy thật; XAML compile, unit test hoặc mockup không thay thế screenshot.
- Startup maximized trong work area của monitor hiện tại; không đổi display resolution, không exclusive fullscreen và
  không che taskbar.
- Reference viewport 1920×1080; minimum usable viewport 1366×768; kiểm tra 100%, 125%, 150% DPI khi môi trường cho phép.
- Primary V1 artifact: self-contained, unpackaged Windows x64 publish directory được scan sạch rồi đóng ZIP.
- User flow: download ZIP → extract → double-click `AuditionModStudio.App.exe` → chạy không installer/admin/.NET SDK.
- Không bundle `acv.exe`, `texconv.exe`, archive/template/keydat/game asset khi chưa có quyền phân phối bằng văn bản.
- Kết thúc bằng checkpoint Product Owner. Không tự chuyển PLAN 102.

### PLAN 102 — Portable Automatic Updater

- Bug-Fix Release Channel.

### PLAN 103 — Marketing Landing Page

- GitHub + Netlify.

### PLAN 104 — Hosted Supabase Production Integration

### PLAN 105 — Trạm Sáng Tạo AI Provider Integration

### PLAN 106 — SePay Automatic Payment Integration

### PLAN 107 — Commercial End-to-End Integration

### PLAN 108 — Low-Spec Performance

- Stability.
- Responsiveness.
- UX Hardening.

### PLAN 109 — V1 Release Candidate

- Final Product Release Gate.

## Target phần cứng tạm thời cho V1

- Windows 10/11 x64.
- CPU phổ thông 4 core.
- RAM mục tiêu tối thiểu 8 GB.
- Cho phép integrated graphics.
- Khuyến nghị SSD.
- Viewport dùng được tối thiểu 1366×768; viewport tham chiếu 1920×1080.

Đây là target tạm thời cho đến khi PLAN 108 đo và xác nhận; không được quảng bá là minimum hardware chính thức.

## STOP GATE

Sau PLAN 101, trạng thái tổng thể phải là `WAITING_PRODUCT_OWNER` cho tới khi Product Owner xem ứng dụng/screenshot và
trả lời `APPROVED`. Không triển khai PLAN 102 trước checkpoint đó.
