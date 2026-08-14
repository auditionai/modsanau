# Bảo mật riêng tư cho logging và gói chẩn đoán

## Phạm vi PLAN 87

Ứng dụng chỉ ghi log vận hành cục bộ để chẩn đoán lỗi và sự kiện bảo mật. Không có telemetry, upload tự động hoặc
đích mạng mới. File log được rolling theo ngày/kích thước, tối đa 14 file được giữ bởi cấu hình hiện tại và mỗi segment
tối đa 10 MiB.

Mọi file log production của desktop đi qua formatter redaction tại sink. Gateway thay các provider mặc định bằng
console provider có cùng redactor. Hai lớp xử lý che:

- password;
- bearer/auth token và JWT;
- signed URL;
- provider/API secret;
- payment/webhook secret.

Structured property có tên nhạy cảm bị thay toàn bộ giá trị trước khi format. Text và exception tiếp tục được quét theo
pattern bằng regex non-backtracking. Không ghi raw prompt, ảnh, template, Authorization header, request body hoặc full
local path theo chủ ý thiết kế.

## Diagnostic export

`IDiagnosticExportService` tạo ZIP bất đồng bộ, hỗ trợ hủy và promote nguyên tử sang destination do người dùng chọn.
Mặc định và hiện tại chỉ có allowlist:

- `diagnostic-manifest.json` chứa version/runtime/OS/architecture, số log và danh mục bị loại trừ;
- tối đa 14 file log đã đổi thành tên tuần tự và được redact lần hai.

ZIP không chứa project, ảnh, template, archive, workspace, secure template cache, settings hoặc credential. Source path
và tên file log gốc không được ghi vào bundle. Request bật `IncludeUserContent` bị từ chối; PLAN 87 không có opt-in user
content. Destination đã tồn tại không bị ghi đè. Path traversal, reparse point, file quá giới hạn, log nhị phân/UTF-8 lỗi
và cancellation đều fail có cấu trúc; temporary bundle được cleanup best-effort.

Bundle chỉ tồn tại tại vị trí người dùng chọn, không tự upload. Người dùng hoặc support operator chịu trách nhiệm truyền,
lưu giữ và xóa bundle theo chính sách hỗ trợ thực tế. Repository chưa chứng minh consent flow, support retention, SIEM,
deployment monitoring hoặc deletion SLA production.

## Giới hạn còn lại

Redaction là defense-in-depth, không phải DLP tuyệt đối. Một secret không có tên/prefix/pattern nhận diện được, hoặc dữ liệu
nhạy cảm tùy ý nằm trong text, vẫn có thể lọt qua. Vì vậy code gọi logger vẫn phải dùng field allowlist và không đưa raw
user content/secret vào log. Same-user hoặc Administrator có thể đọc log/bundle trên máy; ACL, OS account và nơi người dùng
chép bundle vẫn là trust boundary. Trạng thái là `IMPLEMENTED / LOCALLY VERIFIED`; quy trình support và deployment thật
chưa được xác minh.
