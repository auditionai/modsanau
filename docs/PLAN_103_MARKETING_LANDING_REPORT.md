# PLAN 103 — Báo cáo Marketing Landing và External Integration

Ngày hoàn tất local: **2026-08-14**

Quy trình: **PLAN → BUILD → TEST → BÁO CÁO → DỪNG**

## Kết quả

PLAN 103 đã tạo cây public chuyên dụng tại `public-release/`, không công khai lịch sử/source desktop. Landing page tĩnh tiếng Việt có hero, quy trình file-only, tính năng, gallery ảnh WinUI thực tế, ranh giới an toàn, trạng thái phát hành, FAQ, privacy/terms draft, SEO và security headers. Website không có analytics, form, Supabase client hoặc public-download link.

Thiết kế dùng màu blue/coral/aqua của sản phẩm, system font, target tương tác tối thiểu 44 px, focus rõ, menu keyboard/Escape, semantic landmark, `prefers-reduced-motion` và breakpoint responsive. Brand mark SVG code-native thay asset placeholder của WinUI. Ảnh desktop/mobile đã được render bằng Chrome headless và kiểm tra trực quan tại `artifacts/plan-103-web-evidence/`.

## Biên giới public và release

- Manifest `public-allowlist.json` là deny-by-default, liệt kê chính xác 27 file repository và 14 file deploy.
- Scanner chặn symlink, file lạ, binary/archive, private key, credential và mẫu secret phổ biến.
- Build zero-dependency chỉ copy source website allowlisted vào `dist/`.
- Cây public độc lập được export vào ignored artifact, khởi tạo Git branch `develop` và commit một lần.
- Không có source/test desktop, ACV tool, fixture, DDS private, PDB, log, `.env`, key, binary hoặc ZIP trong public inventory.
- Artifact internal PLAN 102 không được duyệt public và không được liên kết/tải lên.

## Kiểm thử

Lệnh `npm run check` PASS trên cả source public và standalone public Git:

- Public repository inventory: **27/27 PASS**.
- Site pages: **4/4 PASS** về semantic/a11y/link/release gate.
- Build deploy inventory: **14/14 PASS**.
- Security headers/CSP: **PASS**.
- Chrome render desktop 1440×1100 và narrow mobile 500×900: **PASS sau sửa grid min-width và brand asset**.
- Regression suite ngoài `IntegrationTests`: **1.075 PASS, 5 SKIP, 0 FAIL**.
- Full solution: các project test thường PASS, nhưng `IntegrationTests` ghi **13 FAIL** vì môi trường không có approved
  `texconv.exe` qua `AUDITION_DIRECTXTEX_TEXCONV_PATH` và private fixture. Các failure đều mang thông báo dynamic-skip/prerequisite;
  PLAN 103 không sửa hay làm yếu test để biến chúng thành PASS.

Public Git:

- Branch: `develop`.
- Source commit: `83ea1adef15471e200661135c96df2e4d2f0f426`.
- Remote develop commit sau merge lịch sử khởi tạo: `6d0fe33cda0098bfb556b1e9a25682a0fbe69b21`.
- Remote: `https://github.com/auditionai/modsanau.git`.
- GitHub Actions run `31786159841`: **success**.

## External integration

| Yêu cầu | Kết quả |
|---|---|
| GitHub exact repository identity | **VERIFIED** — `auditionai/modsanau` |
| GitHub `develop` push | **VERIFIED / PUSHED** — remote `6d0fe33cda0098bfb556b1e9a25682a0fbe69b21` |
| GitHub CI | **PASS** — run `31786159841` |
| `main` production | **UNTOUCHED** — vẫn ở `694a6f8b8283ed94d64802d048575c4d8f55ac0d` |
| Netlify `develop` deploy | **NOT CONFIGURED remotely** — branch URL vẫn HTTP 404 |
| Netlify develop URL | `https://develop--modsanau.netlify.app/` — **HTTP 404** |
| Production target | `https://modsanau.netlify.app/` phản hồi Netlify HTTP 404 khi kiểm tra |
| Production site changed | **NO** |
| Supabase target | `https://plvuutsjwsawkkrmvigz.supabase.co` (Product Owner cung cấp) |
| Supabase admin/schema/env | **NOT ACCESSIBLE / NOT CONFIGURED / NOT REQUIRED** trong PLAN 103 |
| PLAN 104 | **NOT STARTED** |

Chi tiết trạng thái và dashboard checklist nằm tại [PLAN_103_EXTERNAL_INTEGRATION_SETTINGS.md](PLAN_103_EXTERNAL_INTEGRATION_SETTINGS.md).

## Giới hạn và bước tiếp theo được phép

GitHub `develop` đã được publish và CI PASS. Netlify branch deploy vẫn cần xác minh dashboard connection/build settings vì URL dự kiến đang trả 404. Không merge/promote `main` và không mở public download nếu chưa có phê duyệt PLAN riêng.

**DỪNG sau PLAN 103. Không tự chuyển PLAN 104.**
