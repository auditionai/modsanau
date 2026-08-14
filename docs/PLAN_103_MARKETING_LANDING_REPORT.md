# PLAN 103 — Báo cáo Marketing Landing và External Integration

Ngày hoàn tất: **2026-08-14**

Quy trình: **PLAN → BUILD → TEST → BÁO CÁO → DỪNG**

## Kết quả

PLAN 103 đã tạo và phát hành bản landing page **v0.5.0** trên nhánh `develop`. Sau vòng phản hồi mới nhất, khu vực “Các công cụ chính” đã được thiết kế lại hoàn toàn thành technology command deck tương tác, thay cho bố cục thẻ 2×2 cũ.

Command deck gồm rail chọn bốn module Image Lab, DDS Matrix, Visual Diff và Archive Core; viewport mô phỏng trực quan ở trung tâm; telemetry ở cạnh phải. Mỗi module có scene riêng, chuyển trạng thái bằng chuột hoặc bàn phím, cùng các hiệu ứng scan, orbit, packet flow, waveform, holographic border và ánh sáng theo ngữ cảnh. Bố cục responsive chuyển thành dạng dọc trên màn hình nhỏ và tôn trọng `prefers-reduced-motion`.

Typography giữ tỷ lệ vừa phải nhưng bổ sung spectral gradient, underline ánh sáng và chuyển màu cho từ khóa nổi bật. Hệ hiệu ứng gồm sao bay/twinkle, nebula drift, spotlight con trỏ, chiều sâu cửa sổ sản phẩm, hover lighting, card lift, orbit, caret, progress theo cuộn và reveal ngắn. `prefers-reduced-motion` vẫn được hỗ trợ để bảo đảm khả năng tiếp cận.

Landing bổ sung AI Studio với bảy tác vụ Generate/Edit/Inpaint/Outpaint/Remove Object/Replace Object/Upscale, mô tả đúng luồng preview → user approval → DDS validation. Hai bảng pricing mới gồm thuê ứng dụng tuần/tháng/năm và nạp 100/500/1.000 Credits. Vì repository chưa có catalog thương mại được Product Owner phê duyệt, giá tiền được ghi rõ `Chưa công bố`, không tự bịa giá hoặc tạo checkout giả.

Ảnh sản phẩm WinUI thực tế dùng `object-fit: contain` trong khung 16:9, hiển thị nguyên ảnh và không bị crop/zoom. Website không có analytics, form, Supabase client hoặc public-download link.

## Biên giới public và release

- Cây public chuyên dụng nằm tại `public-release/`; repository desktop chính không có remote và không bị công khai.
- Manifest `public-allowlist.json` deny-by-default liệt kê đúng 27 file repository và 14 file deploy.
- Scanner chặn symlink, file lạ, binary/archive, private key, credential và mẫu secret phổ biến.
- Build zero-dependency chỉ sao chép source website allowlisted vào `dist/`.
- Không có source/test desktop, ACV tool, fixture, DDS private, PDB, log, `.env`, key, binary hoặc ZIP trong public inventory.
- Artifact internal PLAN 102 không được đưa lên public hoặc liên kết tải xuống.

## Kiểm thử

`npm run check` PASS trên cả `public-release/` và standalone public Git:

- Public repository inventory: **27/27 PASS**.
- Site pages: **4/4 PASS** về semantic, accessibility, link và release gate.
- Build deploy inventory: **14/14 PASS**.
- Security headers/CSP: **PASS**.
- Render Chrome desktop/mobile và kiểm tra trực quan hero, feature chapters, gallery: **PASS**.
- Ảnh sản phẩm giữ đúng tỷ lệ, không crop/zoom: **PASS**.

## GitHub và Netlify

| Hạng mục | Kết quả |
|---|---|
| Root source commit giao diện v5 | `1672d13` |
| Public Git repository | `https://github.com/auditionai/modsanau.git` |
| Public branch | `develop` |
| Public commit đã push | `c4fdd2e` |
| GitHub Actions | **PASS** — run `31797543312` |
| Netlify branch deploy | **LIVE / HTTP 200** |
| URL develop | `https://develop--modsanau.netlify.app/` |
| Production hostname | `https://modsanau.netlify.app/` — HTTP 200 tại lần kiểm tra cuối |
| PLAN 104 | **NOT STARTED** |

Trong lúc fetch trước khi push, `origin/main` đã đổi từ `694a6f8...` sang `b3618f6fa07aa48148c2bdd58b837538e7253f8a` do tác động bên ngoài. Lần triển khai này **không checkout, merge, commit hoặc push `main`**; chỉ push `develop`. Vì vậy trạng thái HTTP 200 của production không được ghi nhận là kết quả của lần triển khai này.

Chi tiết tích hợp nằm tại [PLAN_103_EXTERNAL_INTEGRATION_SETTINGS.md](PLAN_103_EXTERNAL_INTEGRATION_SETTINGS.md).

## Kết luận gate

Landing page v0.5.0, GitHub `develop`, CI và Netlify branch deploy đều **PASS**. Bản live đã xác nhận có `data-tool-deck` và `tool-tab-editor`. Không promote sang production, không mở public download và không bắt đầu PLAN 104.

**DỪNG sau PLAN 103.**
