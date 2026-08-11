# Design System

PLAN 40 định nghĩa một design system WinUI 3 dùng chung cho Audition AI Mod Studio. Hệ thống ưu tiên phân cấp rõ, contrast, keyboard focus và chi phí render ổn định; gaming visual chỉ là lớp nhận diện, không được che khuất nội dung hoặc trạng thái thao tác.

## Kiến trúc token

Resource được merge theo đúng thứ tự:

1. `PrimitiveTokens.xaml`: raw color, spacing theo nhịp 4 px, radius, typography và motion duration.
2. `SemanticTokens.xaml`: ý nghĩa theo theme như background, surface, text, accent, focus và status; có `Default`, `Light`, `HighContrast` cùng một key contract.
3. `ComponentTokens.xaml`: brush/padding riêng cho panel, card, button, badge và status; chỉ tham chiếu semantic/primitive token.
4. `Components.xaml`: reusable WinUI styles/templates; không chứa raw hex color.

Component mới phải dùng `Ams...` component token trước. Chỉ thêm primitive khi thật sự cần một giá trị nền tảng mới; theme variation phải đổi ở semantic layer, không fork component template.

## Component hiện có

- `AmsGamingPanelStyle`: shared acrylic/fallback panel với border và rounded surface.
- `AmsCardStyle`: card chuẩn có border, padding và subtle `ThemeShadow` token.
- `AmsAccentCardStyle`: card gradient cho vùng nhấn có chủ đích; không dùng cho body dài.
- `AmsPrimaryButtonStyle` và `AmsSecondaryButtonStyle`: cùng depth template, hover lift, pressed travel/scale, disabled opacity và system keyboard focus visual.
- `AmsStatusBadgeStyle`: compact pill container; caller phải kết hợp text/icon, không chỉ dựa vào màu.
- `AmsSectionTitleStyle` và `AmsBodyTextStyle`: typography hierarchy thống nhất.

## Quy tắc sử dụng

- Dùng content rõ ràng trước decoration; tối đa một accent-gradient surface nổi bật trong một vùng nhìn.
- Không hard-code color/spacing/radius trong page hoặc component mới khi token tương ứng đã tồn tại.
- Trạng thái disabled/loading/error phải có semantic text hoặc icon, không chỉ đổi màu.
- Giữ system focus visual và automation semantics của control WinUI. Không thay Button bằng clickable `Border`.
- Animation hover/press ngắn và chỉ thay opacity/transform; không animate layout, blur radius lớn hoặc full-window acrylic.
- Không load font, image, XAML dictionary hay theme từ network/user-controlled URI.

PLAN 40 chỉ cung cấp resource/component foundation. Navigation, App Shell, screen layout, texture grid/editor và workflow binding bắt đầu ở PLAN 41 hoặc PLAN được phê duyệt tương ứng.
