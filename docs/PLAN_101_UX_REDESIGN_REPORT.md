# PLAN 101-UX — Báo cáo tái thiết kế giao diện sản phẩm

Status: `WAITING_PRODUCT_OWNER_VISUAL_APPROVAL`

Production HEAD ban đầu: `69c3688b8eab552badfb475237898a3c5b8c971f`

Commit: `NONE`

Thời điểm chốt kiểm chứng: 2026-08-14 (Asia/Saigon).

## 1. Phạm vi và ranh giới

PLAN 101-UX chỉ tái thiết kế presentation layer WinUI hiện có. Không thay framework, không thêm route giả, không thay backend, archive/DDS/image/AI abstraction hoặc ranh giới file-only. Ứng dụng vẫn kết thúc ở standalone `.ab`/`.acv`, UI không gọi `acv.exe` trực tiếp và không tương tác installation/process/game runtime.

Không commit, reset, clean hoặc ghi đè các thay đổi đang có từ PLAN 101/101-R/101-E. Không chuyển sang PLAN 102.

## 2. Audit giao diện trước redesign

Các vấn đề chính được xác nhận từ cửa sổ WinUI thật ở 1920×1080:

- Nội dung chỉ dùng khoảng 55–70% bề ngang, tạo vùng đen trống rất lớn bên phải.
- Shell, titlebar và sidebar rời rạc; navigation giống prototype hơn sản phẩm desktop hoàn chỉnh.
- Home dùng chuỗi card dọc, CTA và tiến trình tạo project thiếu hierarchy.
- Workspace chưa làm canvas/grid thành trọng tâm; folder browser, texture surface và inspector có trọng lượng gần bằng nhau.
- Image Editor thiên về form, canvas yếu, crop/resize và Before/After thiếu mô hình editor chuyên nghiệp.
- AI Studio giống màn hình gọi API; preview, prompt và advanced controls chưa phân tầng.
- Account/Settings có quá nhiều khoảng trống không chủ đích và chưa diễn đạt tốt trạng thái offline.
- Tonal hierarchy nông; panel/card/status/disabled state khó phân biệt; typography và spacing chưa thành hệ thống.

## 3. Các hướng thiết kế đã cân nhắc

1. **Premium Creative Studio — được chọn:** graphite nhiều lớp, indigo có kiểm soát, cyan chỉ dùng làm signal; workspace trung tâm chiếm ưu thế; một primary CTA trên mỗi vùng tác vụ.
2. **Tactical Mod Lab:** giàu chất gaming/cyber hơn nhưng quá nhiều tín hiệu trang trí, dễ giảm độ tin cậy của công cụ chuyên nghiệp.
3. **Pro Dark Productivity:** rất rõ và tiết chế nhưng quá gần dashboard quản trị, chưa thể hiện đủ tính sáng tạo của editor.

Hướng được chọn kết hợp creative editor, gaming software và professional productivity mà không dùng neon/gradient quá mức.

## 4. Ứng dụng nguyên tắc UI/UX Pro Max

Đã đọc toàn bộ skill `ui-ux-pro-max` và áp dụng thủ công checklist liên quan:

- accessibility-first; target tương tác chính tối thiểu 44 px, navigation 48 px;
- hierarchy bằng contrast, spacing và typography, không chỉ bằng màu;
- semantic token ba lớp, nhịp spacing theo 4/8 và một primary CTA;
- focus visual, keyboard/accessibility name, trạng thái disabled/offline rõ ràng;
- canvas/editor chiếm ưu thế, inspector có chiều rộng ổn định;
- chuyển động ngắn 160–220 ms, không dùng blur/Acrylic lớn hoặc shadow đắt trên surface chính;
- không tạo recent project, số credit, texture hoặc AI result giả.

Script bắt buộc `scripts/search.py --design-system` của skill không tồn tại trong package cục bộ: thư mục `scripts` và `data` chỉ là pointer tới `../../../src/ui-ux-pro-max/...`, nhưng target không có trên máy. Không cài thêm công cụ; checklist của skill được thực hiện trực tiếp trên source và cửa sổ thật.

## 5. Design system sau redesign

- Primitive: graphite/indigo/cyan palette, spacing 4–64, radius 6/10/16/pill, typography caption đến display, motion fast/standard.
- Semantic: window/shell/sidebar/surface/canvas, border subtle/strong, text primary/secondary/muted/disabled, accent primary/subtle, success/warning/error/info/focus cho Default/Light/High Contrast.
- Component: low panel, editor surface, empty state, primary/secondary/ghost/toolbar/icon/danger button, display/page/section/body/label/caption text, navigation item và input.
- Giữ ARGB literal trong semantic `<Color>` để không tái phát `XamlParseException` của PLAN 101-E.
- Loại Acrylic/ThemeShadow khỏi các panel lớn để tránh chi phí render không cần thiết.

## 6. Thay đổi theo surface

### Shell và navigation

- Custom titlebar 44 px tích hợp branding, portable badge và caption controls native.
- NavigationView full-stretch, pane 248 px, compact 68 px; Account/Settings nằm ở footer.
- Mọi page/content control stretch toàn bộ viewport, loại bỏ vùng đen trống bên phải.

### Home

- Hero full-width với proposition và workflow Create → Edit → Export.
- Luồng tạo project trở thành surface ngang ba bước: game, Mod Type, project details.
- CTA tập trung; empty state thật thay cho recent project giả.

### Project Workspace / Texture Grid / Inspector

- Ba vùng rõ ràng theo tỷ lệ `0.85* / 2.7* / 0.95*`.
- Texture Grid là vùng chính; folder browser và Metadata/Edit options là vùng hỗ trợ.
- Preview empty state, filters, inspector và Build & Export được phân tầng lại.
- Build workflow hiển thị Validate → Build → Verify → Export mà không giả trạng thái thực thi.

### Image Editor / Crop & Resize / Before–After

- Canvas `3*` chiếm ưu thế, inspector 390 px.
- Crop/resize status strip, controls theo nhóm, pan/reset/update/apply có cấp bậc rõ ràng.
- Before/After dùng checkerboard, mode/zoom/camera controls và hai viewport cân bằng.

### AI Studio

- Creative canvas `2.6*` và control panel 420 px.
- Prompt là tác vụ chính; negative prompt/model/quality/aspect/output được đưa vào Advanced settings.
- Empty preview và history offline đều là trạng thái thật.

### Account và Settings

- Account có ba vùng account/credits/activity ngay cả khi trusted service offline, không tạo dữ liệu giả.
- Settings bỏ gradient quảng bá lớn; Appearance và About là hai card chính, diagnostics nằm trong Expander.

## 7. Source và test contract thay đổi

Các file UI chính:

- `src/AuditionModStudio.App/MainWindow.xaml`
- `src/AuditionModStudio.App/MainPage.xaml` và `.xaml.cs`
- `src/AuditionModStudio.App/DesignSystem/PrimitiveTokens.xaml`
- `src/AuditionModStudio.App/DesignSystem/SemanticTokens.xaml`
- `src/AuditionModStudio.App/DesignSystem/ComponentTokens.xaml`
- `src/AuditionModStudio.App/DesignSystem/Components.xaml`
- `src/AuditionModStudio.App/Home/HomePage.xaml`
- `src/AuditionModStudio.App/Workspace/ProjectWorkspacePage.xaml`
- `src/AuditionModStudio.App/Editor/ImageEditorPage.xaml`
- `src/AuditionModStudio.App/AiStudio/AiStudioPage.xaml`
- `src/AuditionModStudio.App/Account/AccountPage.xaml`
- `src/AuditionModStudio.App/Settings/SettingsPage.xaml`

Contract cũ được cập nhật theo layout mới nhưng không làm yếu chức năng. Thêm `tests/IntegrationTests/Plan101UxRedesignContractTests.cs` để khóa token hierarchy, 44/48 px target, full-width layout, canvas dominance và việc không dùng ThemeShadow lớn.

## 8. Project/fixture thật và giới hạn acceptance

Repository có fixture thật `015.ab` và tool `acv.exe`. Tuy nhiên production bootstrap cố ý đăng ký:

- `IModCatalog` rỗng;
- `ITextureManifestCatalog` rỗng;
- `UnavailableProjectTemplateAcquisitionService`.

Khi chọn game Audition, UI thật báo `No compatible Mod Types are available for this game yet.` Do đó không thể tạo project production, populate texture grid hoặc chọn texture mà không thêm QA hook/private catalog/template acquisition ngoài phạm vi PLAN, hoặc đưa dữ liệu giả vào sản phẩm.

Vì vậy:

- không tạo ảnh giả `03-project-workspace-populated.png`;
- không tạo ảnh giả `04-texture-selected.png`;
- thay bằng `03-project-workspace-empty-blocked.png`, thể hiện đúng trạng thái production;
- các ảnh Editor/Before–After/Build Export là surface thật nhưng không có project/texture data.

## 9. Bộ ảnh acceptance thật

Tất cả ảnh được chụp bằng `PrintWindow`/screen capture từ EXE Release trong ZIP đã extract, không phải mockup hoặc ảnh tạo:

| Ảnh | Kích thước | Ghi chú |
|---|---:|---|
| `artifacts/plan-101ux-acceptance/01-home.png` | 1936×1048 | Home redesign |
| `artifacts/plan-101ux-acceptance/02-project-create.png` | 1936×1048 | Audition được chọn; blocker Mod Type thật |
| `artifacts/plan-101ux-acceptance/03-project-workspace-empty-blocked.png` | 1936×1048 | Workspace production chưa có project |
| `artifacts/plan-101ux-acceptance/05-image-editor.png` | 1936×1048 | Canvas + inspector |
| `artifacts/plan-101ux-acceptance/06-crop-resize.png` | 1936×1048 | Crop & Resize surface |
| `artifacts/plan-101ux-acceptance/07-before-after.png` | 1936×1048 | Before/After surface |
| `artifacts/plan-101ux-acceptance/08-build-export.png` | 1936×1048 | Build & Export surface |
| `artifacts/plan-101ux-acceptance/09-ai-studio.png` | 1936×1048 | AI Studio offline-ready state |
| `artifacts/plan-101ux-acceptance/10-account.png` | 1936×1048 | Account offline state |
| `artifacts/plan-101ux-acceptance/11-settings.png` | 1936×1048 | Settings redesign |
| `artifacts/plan-101ux-acceptance/12-full-app-1920x1080.png` | 1920×1080 | Full desktop reference |

Ảnh 1366×768 là optional và không được tạo trong phiên này.

## 10. Build và test

- Debug x64 solution: `PASS`, 0 warning, 0 error.
- Release x64 solution: `PASS`, 0 warning, 0 error.
- Targeted UI/regression ban đầu: 23 passed.
- Contract UI cuối cho PLAN 43/44/45/46/101-UX: 7 passed.
- Integration không phụ thuộc external fixture: 269 passed, 1 skip chuẩn, 0 failed.
- Core: 145 passed.
- DDS: 105 passed.
- Projects: 153 passed.
- Gateway: 131 passed, 12 skipped do database integration prerequisites.
- Archives: 130 passed, 1 skipped.
- Security: 121 passed.
- Imaging: 262 passed.

Full integration runner còn biểu diễn 13 `$XunitDynamicSkip$` thành failed khi không có approved `texconv.exe`/private fixtures của PLAN 14/15/50/55/98/100. Đây là external prerequisite đã biết, không phải regression UX. Bốn contract UI từng fail vì nhãn redesign đã được khôi phục trong source và chạy lại PASS; không sửa test để che lỗi.

## 11. Portable release gate

Artifact cuối:

`artifacts/plan-101ux-portable/1.0.0-internal.101ux/AuditionAI-Mod-Studio-1.0.0-internal.101ux-win-x64.zip`

- ZIP bytes: `96,187,041`.
- SHA-256: `C17E8E4A55EBA3D8EDC78754B4E502FEC2992BD0D2BC35F6279F4453180C7572`.
- Payload: 558 file, 249,031,336 bytes.
- Release artifact policy: `PASS`.
- Secret scan: `PASS`.
- ZIP → extract sạch → `AuditionModStudio.App.exe`: `PASS`.
- Process: `Responding=true`, title `Audition AI Mod Studio`.
- `early-startup.log`: 0 byte.
- Không yêu cầu admin, installer hoặc MSIX registration.

## 12. Disk cleanup

Đã dừng process acceptance và xoá vĩnh viễn đúng năm thư mục publish/extract trung gian đã xác minh:

- `artifacts/plan-101ux-pass1`
- `artifacts/plan-101ux-extracted-final`
- `artifacts/plan-101ux-extracted-pass2`
- `artifacts/plan-101ux-extracted-pass3`
- `artifacts/plan-101ux-portable/1.0.0-internal.101ux/staging`

Tổng dữ liệu tạm đã bỏ: `1,334,762,765` byte (khoảng 1.33 GB). Các thư mục này không còn khôi phục trực tiếp, nhưng có thể tái tạo từ source/ZIP. ZIP cuối, report JSON và ảnh acceptance được giữ lại. Ổ E còn khoảng 27.78 GB trống tại thời điểm chốt.

## 13. Kết luận và Product Owner action

Redesign đã xử lý vùng trống lớn ở 1920×1080, đưa shell và từng surface về một ngôn ngữ premium creative editor nhất quán, giữ nguyên chức năng/ràng buộc bảo mật và không tạo dữ liệu acceptance giả.

Hành động đề nghị: Product Owner review bộ ảnh và ZIP internal QA. Không triển khai PLAN 102 hoặc thay framework cho tới khi có phê duyệt riêng.

`WAITING_PRODUCT_OWNER_VISUAL_APPROVAL`

## 14. Phụ lục chốt bản địa hóa vi-VN

Phụ lục này thay thế các giá trị artifact, ảnh và kết luận ngôn ngữ tại mục 8–12 ở trên. Toàn bộ giao diện production của bản nghiệm thu đã được chuyển sang tiếng Việt; tên thương hiệu `Audition AI Mod Studio` và các thuật ngữ kỹ thuật được Product Owner cho phép vẫn giữ nguyên.

### 14.1 Kiến trúc bản địa hóa và thuật ngữ

- Ngôn ngữ mặc định/trung lập của ứng dụng: `vi-VN` qua `DefaultLanguage` và `NeutralLanguage`.
- Resource tập trung: `src/AuditionModStudio.App/Strings/vi-VN/Resources.resw`; các heading chính dùng `x:Uid` để sẵn sàng bổ sung locale khác về sau.
- Nhãn enum hiển thị cho người dùng đi qua `VietnameseEnumLabelConverter`; không hiển thị trực tiếp tên enum tiếng Anh.
- Chuỗi trạng thái động, lỗi, cảnh báo, tiến trình và empty state trong ViewModel đã được viết lại bằng tiếng Việt tự nhiên.
- Từ điển chuẩn: `docs/UI_VIETNAMESE_TERMINOLOGY.md`.
- Không triển khai bộ chuyển ngôn ngữ vì không nằm trong phạm vi V1.

Kết quả cổng ngôn ngữ cuối:

| Phân loại | Kết quả |
|---|---|
| `ALLOWED_BRAND` | `Audition AI Mod Studio` |
| `ALLOWED_TECHNICAL_TERM` | AI, DDS, BC3, DXT5, Alpha, Credits, Portable, x64, Build, Texture, SHA-256 |
| `NOT_USER_VISIBLE` | định danh source, namespace, enum, mã trạng thái và contract nội bộ bằng English |
| `NEEDS_TRANSLATION` | **0** |

Các cụm `archive` còn tìm thấy trong source đều thuộc định danh kỹ thuật như `IArchiveExportService`, enum hoặc mã trạng thái `extracting_archive`; không còn chuỗi `archive` hiển thị cho người dùng. Giao diện dùng “tệp nguồn” cho đầu vào nguyên bản và “tệp Mod” cho đầu ra.

### 14.2 Kiểm tra UI/UX tiếng Việt

Audit theo checklist UI/UX Pro Max đạt các điểm sau:

- phân cấp nội dung, độ tương phản và mật độ copy: `PASS`;
- dấu tiếng Việt ở heading, button, ComboBox, menu và nội dung mô tả: `PASS`, không clipping;
- touch target 44 px và căn dọc button: `PASS`;
- sidebar và bố cục ở 1920×1080: `PASS`;
- cửa sổ 1366×768: `PASS`, nội dung chính vẫn dùng được và không chồng chữ;
- khu vực `Build & Xuất file`: `PASS` sau khi tách hàng cho tùy chọn thay file, trạng thái và nút thao tác.

Không đưa dữ liệu dự án/Texture giả vào bản production. Trạng thái workspace trống trong ảnh là trạng thái thật do production mod catalog và template acquisition chưa khả dụng trong phạm vi PLAN 101-UX.

### 14.3 Build và kiểm thử cuối

- App Release x64: `PASS`, 0 warning, 0 error.
- Nhóm hồi quy localization/Build Export/Project Workspace cuối: 18 passed, 0 failed, 0 skipped.
- Nhóm UI/localization bị tác động trước bước chốt: 30 passed, 0 failed.
- Integration không phụ thuộc external fixture: 270 passed, 1 skipped chuẩn, 0 failed.
- Full integration: 274 passed, 1 skipped chuẩn; 13 dynamic skip được runner biểu diễn thành `$XunitDynamicSkip$` do thiếu `texconv.exe` đã phê duyệt và private fixtures PLAN 14/15/50/55/98/100. Không có regression WinUI/localization.
- Release artifact policy: `PASS`; secret scan: `PASS`.

### 14.4 Portable artifact cuối

`artifacts/plan-101ux-portable/1.0.0-internal.101ux.vi/AuditionAI-Mod-Studio-1.0.0-internal.101ux.vi-win-x64.zip`

- Phiên bản: `1.0.0-internal.101ux.vi`.
- ZIP: 96,190,886 byte.
- SHA-256: `3C1E9916882754FB385C38F2D60A3D842F6EADCC0804B5CAF2E3EE6F4ACBD9F6`.
- Payload: 558 file, 249,036,645 byte.
- ZIP → giải nén sạch → chạy `AuditionModStudio.App.exe`: `PASS`.
- Process: `Responding=true`; title `Audition AI Mod Studio`; cửa sổ thật hiển thị được.

### 14.5 Bộ ảnh Pass 2 tiếng Việt

Tất cả ảnh được chụp từ EXE Release trong chính ZIP có SHA-256 ở mục 14.4, không phải mockup hoặc ảnh tạo:

| Ảnh | Kích thước | Nội dung |
|---|---:|---|
| `artifacts/plan-101ux-acceptance-vi/01-home-pass2-vi.png` | 1936×1048 | Trang chủ và tạo dự án |
| `artifacts/plan-101ux-acceptance-vi/02-projects-pass2-vi.png` | 1936×1048 | Điều hướng Dự án |
| `artifacts/plan-101ux-acceptance-vi/03-project-workspace-pass2-vi.png` | 1936×1048 | Không gian dự án, trạng thái production thật |
| `artifacts/plan-101ux-acceptance-vi/04-build-export-pass2-vi.png` | 1936×1048 | Build & Xuất file |
| `artifacts/plan-101ux-acceptance-vi/05-editor-pass2-vi.png` | 1936×1048 | Trình chỉnh sửa ảnh |
| `artifacts/plan-101ux-acceptance-vi/06-crop-resize-pass2-vi.png` | 1936×1048 | Cắt & đổi kích thước |
| `artifacts/plan-101ux-acceptance-vi/07-before-after-pass2-vi.png` | 1936×1048 | So sánh trước / sau |
| `artifacts/plan-101ux-acceptance-vi/08-ai-pass2-vi.png` | 1936×1048 | AI Studio |
| `artifacts/plan-101ux-acceptance-vi/09-account-pass2-vi.png` | 1936×1048 | Tài khoản, trạng thái offline tự nhiên |
| `artifacts/plan-101ux-acceptance-vi/10-settings-pass2-vi.png` | 1936×1048 | Cài đặt |
| `artifacts/plan-101ux-acceptance-vi/11-full-1920-pass2-vi.png` | 1936×1048 | Toàn ứng dụng ở kích thước desktop |
| `artifacts/plan-101ux-acceptance-vi/12-1366-pass2-vi.png` | 1366×768 | Cổng khả dụng ở độ phân giải tối thiểu |

### 14.6 Dọn dẹp và trạng thái bàn giao

- Đã xóa vĩnh viễn artifact/ảnh acceptance tiếng Anh cũ vì bị Product Owner từ chối.
- Đã xóa thư mục giải nén kiểm chứng cuối `artifacts/plan-101ux-vi-extracted`: 249,036,645 byte.
- Đã xóa staging cuối `artifacts/plan-101ux-portable/1.0.0-internal.101ux.vi/staging`: 249,036,645 byte.
- Hai thư mục tạm trên không thể khôi phục trực tiếp, nhưng có thể tái tạo từ source/ZIP; ZIP, report JSON và 12 ảnh tiếng Việt được giữ lại.
- Working tree dirty có chủ đích được bảo toàn; không reset, clean, checkout đè hoặc commit.
- Không triển khai PLAN 102.

Trạng thái chốt:

`WAITING_PRODUCT_OWNER_VISUAL_APPROVAL`

## 15. Creative Daylight — giao diện thay thế theo phản hồi Product Owner

Phụ lục này thay thế toàn bộ hướng mỹ thuật dark và các artifact/ảnh acceptance tại mục 1–14. Product Owner đã từ chối giao diện tối; bản hiện tại được thiết kế lại thành một chủ đề sáng mới, không tái sử dụng sidebar tối, gradient, nút giả chiều sâu, nền tối hoặc bố cục cũ.

### 15.1 Ngôn ngữ thiết kế mới

- Tên chủ đề: `Creative Daylight`; ứng dụng luôn khởi động ở giao diện sáng.
- Màu nền trắng ngà/xám rất nhạt, màu chính cobalt, điểm nhấn coral và trạng thái thành công xanh mint.
- Điều hướng chuyển sang thanh ngang trên cùng; title bar, Home hero, workspace, editor, AI Studio, Account và Settings được bố cục lại.
- Card và nút phẳng, đường viền rõ, chỉ bo góc thông thường; không còn gradient, shadow lớn, hiệu ứng nhấn lún hoặc hình pill.
- Kích thước thao tác tối thiểu 44 px, focus/hover/pressed rõ; `ThemeResource` tiếp tục được dùng cho token giao diện và tương phản cao theo Windows.
- Kết quả tra cứu `ui-ux-pro-max` được dùng để kiểm tra mật độ chữ tiếng Việt, hierarchy, độ tương phản, touch target và khả năng dùng ở 1366×768.

### 15.2 Build, test và language gate

- App Release x64: `PASS`, 0 warning, 0 error.
- Nhóm contract UI/localization bị tác động: 24 passed, 0 failed, 0 skipped.
- Ba hồi quy contract phát hiện trong full integration (`Tệp nguồn`, hướng dẫn tương phản cao, `Thay tệp đang có`) đã được phục hồi trong UI và chạy lại PASS; không hạ hoặc sửa tiêu chí để che lỗi.
- Full integration cuối: 276 passed, 1 skipped chuẩn; 13 `$XunitDynamicSkip$` được runner biểu diễn thành failed do thiếu approved `texconv.exe`/private fixtures của PLAN 14/15/50/55/98/100. Không còn regression UI/localization.
- Cổng ngôn ngữ: `NEEDS_TRANSLATION = 0`; các chuỗi tiếng Anh còn lại thuộc `ALLOWED_BRAND`, `ALLOWED_TECHNICAL_TERM` hoặc `NOT_USER_VISIBLE` theo `docs/UI_VIETNAMESE_TERMINOLOGY.md`.
- Runtime từ ZIP: `PASS`; process phản hồi, cửa sổ thật hiển thị và không phát sinh lỗi startup.

### 15.3 Portable artifact Creative Daylight

`artifacts/plan-101ux-portable/1.0.0-internal.101ux.daylight/AuditionAI-Mod-Studio-1.0.0-internal.101ux.daylight-win-x64.zip`

- Phiên bản: `1.0.0-internal.101ux.daylight`.
- ZIP: 96,189,596 byte.
- SHA-256: `8C102508818AC071E9BBE8A0E6D202A68D97587471DEF9A43BE4EE8B4BD48F04`.
- Payload: 558 file, 249,033,199 byte.
- Release artifact policy: `PASS`; secret scan: `PASS`.
- ZIP → giải nén sạch → `AuditionModStudio.App.exe`: `PASS`.
- Process kiểm chứng: `Responding=true`; title `Audition AI Mod Studio`.

### 15.4 Ảnh acceptance từ đúng ZIP phát hành

Thư mục: `artifacts/plan-101ux-creative-daylight-acceptance`.

| Ảnh | Kích thước | Nội dung |
|---|---:|---|
| `01-home-pass2-vi.png` | 1936×1048 | Trang chủ và quy trình tạo dự án mới |
| `02-projects-pass2-vi.png` | 1936×1048 | Điều hướng Dự án |
| `03-project-workspace-pass2-vi.png` | 1936×1048 | Không gian dự án ba vùng |
| `04-build-export-pass2-vi.png` | 1936×1048 | Build & Xuất tệp Mod |
| `05-editor-pass2-vi.png` | 1936×1048 | Trình chỉnh sửa ảnh |
| `06-crop-resize-pass2-vi.png` | 1936×1048 | Cắt & đổi kích thước |
| `07-before-after-pass2-vi.png` | 1936×1048 | So sánh trước / sau |
| `08-ai-pass2-vi.png` | 1936×1048 | AI Studio |
| `09-account-pass2-vi.png` | 1936×1048 | Tài khoản và trạng thái dịch vụ gián đoạn |
| `10-settings-pass2-vi.png` | 1936×1048 | Cài đặt Creative Daylight |
| `11-full-1920-pass2-vi.png` | 1936×1048 | Cửa sổ desktop đầy đủ |
| `12-1366-pass2-vi.png` | 1366×768 | Cổng khả dụng ở độ phân giải tối thiểu |

Ảnh được chụp bằng `PrintWindow` từ EXE Release trong chính ZIP nêu tại mục 15.3, không phải mockup hoặc ảnh tạo. Kiểm tra trực quan không phát hiện nút bị cắt, dấu tiếng Việt bị clipping, tràn ngang hoặc giao diện dark còn sót.

### 15.5 Dọn dẹp và trạng thái bàn giao

- Đã dừng process acceptance; không còn process Audition Mod Studio chạy nền.
- Đã xóa đúng bốn thư mục tái tạo được: bản giải nén kiểm chứng, staging phát hành và hai bộ ảnh probe; tổng cộng 499,730,376 byte.
- Dữ liệu đã xóa không khôi phục trực tiếp nhưng có thể tái tạo từ source/ZIP. ZIP chốt, manifest/report và 12 ảnh acceptance được giữ lại.
- Working tree dirty có chủ đích được bảo toàn; không reset, clean, checkout đè hoặc commit.
- Không triển khai PLAN 102.

Trạng thái chốt:

`WAITING_PRODUCT_OWNER_VISUAL_APPROVAL`
