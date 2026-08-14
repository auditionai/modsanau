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
| Repository `auditionai/modsanau` | **VERIFIED** | Product Owner xác nhận `https://github.com/auditionai/modsanau.git`; repository public phản hồi HTTP 200. |
| Remote của cây public | **CONFIGURED** | `origin` trỏ đúng `https://github.com/auditionai/modsanau.git`. Repository desktop chính vẫn không có remote và không bị public. |
| Cây public chuyên dụng | **VERIFIED** | `public-release/` dùng deny-by-default allowlist, không xuất toàn bộ repository desktop. |
| Public Git, nhánh `develop` | **VERIFIED** | Local và remote cùng ở `6d0fe33cda0098bfb556b1e9a25682a0fbe69b21`; 27 file allowlisted. |
| Fetch/divergence với GitHub | **VERIFIED** | Remote ban đầu `694a6f8`; local/remote lệch 1/1. Đã merge unrelated histories, giữ README public, không force-push. |
| Push `develop` | **VERIFIED** | Push `694a6f8..6d0fe33` thành công. GitHub Actions run `31786159841` kết luận `success`. |
| `main` production | **VERIFIED — UNTOUCHED** | Remote `main` vẫn chính xác ở `694a6f8b8283ed94d64802d048575c4d8f55ac0d`; không checkout, commit, push hay merge `main`. |
| Public ZIP / GitHub Release asset | **NOT CONFIGURED** | Artifact internal PLAN 102 không được public. Không ZIP nào nằm trong cây public. |

## Netlify

| Hạng mục | Trạng thái | Bằng chứng / quyết định |
|---|---|---|
| Site target | **VERIFIED (hostname only)** | Product Owner cung cấp `https://modsanau.netlify.app/`; HEAD công khai ngày 2026-08-14 phản hồi từ Netlify với HTTP 404. Điều này không chứng minh quyền dashboard. |
| Dashboard/site ownership | **NOT ACCESSIBLE** | Không có authenticated browser automation, Netlify CLI hoặc session dashboard đã xác minh. |
| Build command / publish directory | **CONFIGURED locally** | `npm run build` / `dist` trong `public-release/netlify.toml`. |
| Production branch `main` | **CONFIGURED locally; NOT ACCESSIBLE remotely** | Checklist và config public đã chuẩn bị; dashboard chưa thể xác minh. |
| Branch deploy `develop` | **CONFIGURED locally; NOT ACTIVE remotely** | GitHub push đã hoàn tất nhưng Netlify dashboard/connection chưa xác minh. |
| Develop deploy URL | **NOT CONFIGURED** | `https://develop--modsanau.netlify.app/` trả HTTP 404 sau push; chưa có branch deploy khả dụng. |
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

1. Trên Netlify, xác nhận site đã kết nối đúng repository `auditionai/modsanau`.
2. Xác nhận build `npm run build`, publish `dist`, production branch `main`.
3. Bật branch deploy cho `develop`; kiểm tra deploy log và URL branch trước mọi promote.
4. Không thêm secret đặc quyền; không dùng artifact PLAN 102 làm public download.
5. Chỉ sau gate riêng mới tạo GitHub Release và gắn ZIP đã được phê duyệt làm Release asset.

## Kết luận gate

Local website/configuration và GitHub `develop` push: **PASS**. Netlify branch deploy: **NOT CONFIGURED / HTTP 404**, cần kiểm tra dashboard connection; production không thay đổi. PLAN 104: **NOT STARTED**.
