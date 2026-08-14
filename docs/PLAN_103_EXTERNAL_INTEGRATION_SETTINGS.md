# PLAN 103 — External Integration Settings

Ngày kiểm tra: **2026-08-14**

Nhánh triển khai: **`develop`**

Phạm vi: website public, cấu hình nhánh/deploy và xác nhận dịch vụ; không promote production, không triển khai Supabase schema và không bắt đầu PLAN 104.

## GitHub

| Hạng mục | Trạng thái | Bằng chứng / quyết định |
|---|---|---|
| Repository `auditionai/modsanau` | **VERIFIED** | Remote `https://github.com/auditionai/modsanau.git`. |
| Cây public chuyên dụng | **VERIFIED** | `public-release/` dùng deny-by-default allowlist; repository desktop không bị export. |
| Public Git `develop` | **VERIFIED** | Local và remote cùng tại `c4fdd2e`; 27 file allowlisted. |
| Push redesign v0.5.0 | **VERIFIED** | Push `f3b92f9..c4fdd2e` thành công, chỉ tới `develop`. |
| GitHub Actions | **PASS** | Run `31797543312` hoàn tất với kết luận `success`. |
| `main` production | **UNTOUCHED BY THIS OPERATION** | `origin/main` được phát hiện đã đổi bên ngoài sang `b3618f6fa07aa48148c2bdd58b837538e7253f8a`. Không checkout, merge, commit hoặc push `main`. |
| Public ZIP / Release asset | **NOT CONFIGURED** | Không đưa artifact internal PLAN 102 vào public. |

## Netlify

| Hạng mục | Trạng thái | Bằng chứng / quyết định |
|---|---|---|
| Build / publish | **CONFIGURED** | `npm run build` / `dist` trong `netlify.toml`. |
| Branch deploy `develop` | **VERIFIED LIVE** | `https://develop--modsanau.netlify.app/` phản hồi HTTP 200; nội dung live có `data-tool-deck` và `tool-tab-editor` của landing v0.5.0. |
| Production hostname | **PUBLICLY REACHABLE** | `https://modsanau.netlify.app/` phản hồi HTTP 200 tại lần kiểm tra cuối. |
| Production attribution | **EXTERNAL / NOT ATTRIBUTED TO THIS PUSH** | Lần thực hiện chỉ push `develop`; thay đổi `main` đã tồn tại từ bên ngoài trước push. |
| Dashboard/site ownership | **NOT ACCESSIBLE** | Không có authenticated Netlify dashboard hoặc CLI session để xác minh quyền sở hữu/cài đặt nội bộ. |
| Environment secrets | **NOT REQUIRED** | Website tĩnh không cần Supabase/AI/payment/signing secret; config không chứa secret. |

## Supabase

| Hạng mục | Trạng thái | Bằng chứng / quyết định |
|---|---|---|
| Project target | **VERIFIED (owner-supplied URL)** | `https://plvuutsjwsawkkrmvigz.supabase.co`. |
| Dashboard ownership/settings | **NOT ACCESSIBLE** | Không có session/dashboard credential được ủy quyền. |
| Public web integration | **NOT REQUIRED** | Marketing site không có account, form, analytics hoặc truy cập Supabase. |
| Publishable/anon key | **NOT CONFIGURED** | Không cần và không sao chép vào website. |
| Service-role/elevated secret | **NOT REQUIRED / NOT EXPOSED** | Không đọc, ghi, log hoặc đưa vào client/repository. |
| Schema/RLS/entitlement/Device ID | **NOT STARTED** | Ngoài phạm vi PLAN 103. |

## Gate vận hành

- Preview và nghiệm thu trên URL branch `develop` trước mọi quyết định promote.
- Không thêm secret đặc quyền hoặc dùng artifact PLAN 102 làm public download.
- Chỉ tạo GitHub Release hoặc thay đổi production khi có PLAN/phê duyệt riêng.

## Kết luận

GitHub `develop`, CI và Netlify branch deploy: **PASS**. Production không bị tác động bởi thao tác push này. PLAN 104: **NOT STARTED**.
