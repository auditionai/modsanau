# PLAN 103 — External Integration Settings

Ngày kiểm tra: **2026-08-14**

Nhánh triển khai: **`develop`**

Phạm vi: chuẩn bị website public, cấu hình nhánh/deploy và xác nhận danh tính dịch vụ; **không** vận hành production, không triển khai Supabase schema và không bắt đầu PLAN 104.

## Quy ước trạng thái

- **VERIFIED**: có bằng chứng đọc được trực tiếp trong repository hoặc phản hồi công khai.
- **CONFIGURED**: cấu hình đã được ghi vào cây public và vượt kiểm tra local.
- **NOT CONFIGURED**: chưa có cấu hình thực tế.
- **NOT ACCESSIBLE**: không có quyền/dashboard/remote hoặc công cụ xác thực để kiểm tra.
- **NOT REQUIRED**: không cần cho phạm vi PLAN 103.

## GitHub

| Hạng mục | Trạng thái | Bằng chứng / quyết định |
|---|---|---|
| Tên mục tiêu `modsanau` | **NOT ACCESSIBLE** | Product Owner cung cấp tên nhưng chưa cung cấp owner và URL repository chính xác. Không suy đoán remote. |
| Remote của repository desktop | **NOT CONFIGURED** | `git remote -v` không có kết quả. |
| Cây public chuyên dụng | **VERIFIED** | `public-release/` dùng deny-by-default allowlist, không xuất toàn bộ repository desktop. |
| Public Git local, nhánh `develop` | **VERIFIED** | Commit `83ea1adef15471e200661135c96df2e4d2f0f426`, 27 file allowlisted. |
| Fetch/divergence với GitHub | **NOT ACCESSIBLE** | Không có exact remote; pre-push gate dừng trước fetch/push. |
| Push `develop` | **NOT ACCESSIBLE** | Không có remote đã xác minh; không push. |
| `main` production | **VERIFIED — UNTOUCHED** | Không checkout, commit, push hay merge `main`; public Git local chỉ có `develop`. Repository desktop giữ `master` tại baseline PLAN 102. |
| Public ZIP / GitHub Release asset | **NOT CONFIGURED** | Artifact internal PLAN 102 không được public. Không ZIP nào nằm trong cây public. |

## Netlify

| Hạng mục | Trạng thái | Bằng chứng / quyết định |
|---|---|---|
| Site target | **VERIFIED (hostname only)** | Product Owner cung cấp `https://modsanau.netlify.app/`; HEAD công khai ngày 2026-08-14 phản hồi từ Netlify với HTTP 404. Điều này không chứng minh quyền dashboard. |
| Dashboard/site ownership | **NOT ACCESSIBLE** | Không có authenticated browser automation, Netlify CLI hoặc session dashboard đã xác minh. |
| Build command / publish directory | **CONFIGURED locally** | `npm run build` / `dist` trong `public-release/netlify.toml`. |
| Production branch `main` | **CONFIGURED locally; NOT ACCESSIBLE remotely** | Checklist và config public đã chuẩn bị; dashboard chưa thể xác minh. |
| Branch deploy `develop` | **CONFIGURED locally; NOT ACCESSIBLE remotely** | Context `develop` và `branch-deploy` đã chuẩn bị; chưa push nên không có branch deploy URL. |
| Develop deploy URL | **NOT CONFIGURED** | Không có deploy thực tế trong PLAN 103 vì GitHub remote/dashboard không truy cập được. |
| Production site changed | **VERIFIED — NO** | Không gọi deploy/publish/promote API; public target vẫn phản hồi 404 tại lần kiểm tra. |
| Environment secrets | **NOT REQUIRED** | Website tĩnh không cần Supabase/AI/payment/signing secret. `netlify.toml` không chứa secret. |

## Supabase

| Hạng mục | Trạng thái | Bằng chứng / quyết định |
|---|---|---|
| Project target | **VERIFIED (owner-supplied URL)** | `https://plvuutsjwsawkkrmvigz.supabase.co`; root công khai phản hồi HTTP 404 JSON, chỉ xác nhận hostname có phản hồi. |
| Dashboard ownership / project settings | **NOT ACCESSIBLE** | Không có session/dashboard credential được ủy quyền; không suy diễn từ hostname. |
| Public web integration | **NOT REQUIRED** | Marketing site hiện không có account, form, analytics hay truy cập Supabase. |
| Publishable/anon key | **NOT CONFIGURED** | Không cần và không sao chép vào website. |
| Service-role/elevated secret | **NOT REQUIRED / NOT EXPOSED** | Không đọc, không ghi, không log và không đưa vào client/repository. |
| Schema/RLS/entitlement/Device ID | **NOT STARTED** | Thuộc PLAN 104 hoặc PLAN được duyệt sau; PLAN 103 không thay đổi Supabase. |

## Dashboard checklist sau khi xác minh remote

1. Xác nhận exact GitHub owner/repository URL của public repository `modsanau`.
2. Thêm remote vào **cây public chuyên dụng**, fetch và kiểm tra divergence trước push `develop`.
3. Trên Netlify, xác nhận repository đúng, build `npm run build`, publish `dist`, production branch `main`.
4. Bật branch deploy cho `develop`; kiểm tra deploy log và URL branch trước mọi promote.
5. Không thêm secret đặc quyền; không dùng artifact PLAN 102 làm public download.
6. Chỉ sau gate riêng mới tạo GitHub Release và gắn ZIP đã được phê duyệt làm Release asset.

## Kết luận gate

Local website/configuration: **PASS**. External GitHub push và Netlify branch deploy: **BLOCKED BY UNVERIFIED IDENTITY/ACCESS**, vì vậy dừng an toàn, không thay đổi production. PLAN 104: **NOT STARTED**.
