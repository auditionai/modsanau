# ADR-0001: Template exposure và vị trí build archive

- Trạng thái: Accepted
- Ngày: 2026-08-12
- Chủ sở hữu: Product Architecture, Security, Backend, Desktop
- Quyết định cần được rà soát lại trước commercial release và khi điều khoản phân phối template thay đổi.

## Bối cảnh và ràng buộc

ACV Tool 5 cần archive và extracted folder truy cập được trên filesystem Windows. Khi build local, working archive có thể gần giống pristine archive; standalone `.ab`/`.acv` cuối cùng có thể giữ nguyên nhiều asset gốc và người dùng đã được cấp quyền có thể extract chúng. AES-GCM cache, DPAPI, ACL, cleanup, obfuscation hay đổi extension chỉ giảm casual exposure, không tạo DRM tuyệt đối.

Product Direction Lock yêu cầu pipeline kết thúc tại file export standalone. Backend không được detect/nhận game path, install output, launch game hoặc thực hiện runtime/in-game validation. Server vẫn là authority cho catalog, entitlement, revocation, trusted hash, storage reference và payment; desktop chỉ xử lý bytes đã được cấp quyền.

## Các tiêu chí quyết định

1. Legal/licensing: quyền phân phối archive gốc và asset không do kỹ thuật tự suy đoán; release owner phải có bằng chứng license cho từng template/version.
2. Exposure: ngăn unauthenticated download và casual harvesting, nhưng thừa nhận authorized recipient có thể recover plaintext/final archive.
3. Latency/cost: edit/build lặp lại cần nhanh, không upload source/mask/project archive không cần thiết và không tạo worker cost cho mỗi build.
4. Offline: project/workspace đã tạo phải tiếp tục edit/build/export local; acquisition premium mới vẫn fail-closed khi offline.
5. Compatibility: ACV Tool 5 hiện là Windows executable 32-bit đã kiểm chứng với local isolated working directory.
6. Privacy: project edit và user asset mặc định không rời máy chỉ để build archive.
7. Operations: tránh đưa queue, worker sandbox, retention, regional capacity và recovery server vào critical path khi lợi ích secrecy bị giới hạn bởi output cuối.

## Phương án A — Client-side build

Ưu điểm: latency thấp, tận dụng pipeline PLAN 49–55, hỗ trợ build/export của project hiện hữu khi cloud outage, chi phí server nhỏ và dữ liệu edit ở local.

Nhược điểm: premium package phải materialize plaintext trong controlled workspace; local attacker cùng account/Administrator có thể đọc process/workspace; pristine-derived bytes và final archive có thể bị copy.

Đánh giá: phù hợp kỹ thuật nhưng nếu dùng catalog/package local không có server control sẽ quá yếu. Chỉ chấp nhận khi kết hợp distribution và cache controls.

## Phương án B — Server-side build worker

Ưu điểm: không cần gửi pristine template đầy đủ xuống desktop trước build; có thể centralize package inventory, worker isolation và audit.

Nhược điểm: user input/project delta phải upload; tăng privacy, bandwidth, latency, queue/retry, regional capacity, retention và incident surface. ACV Tool 5 cần Windows worker cùng filesystem isolation/licensing. Final `.ab/.acv` vẫn được tải về và có thể extract, nên không loại bỏ exposure. Cloud outage chặn mọi build và làm yếu local-core isolation.

Đánh giá: không chọn cho V1. Chỉ mở lại bằng PLAN riêng nếu legal bắt buộc không giao pristine-derived package, có Windows worker sandbox được phê duyệt và product chấp nhận online-only build.

## Phương án C — Hybrid

Server kiểm soát catalog, exact version/hash, entitlement/revocation, signed package manifest và private short-lived access. Desktop stream/verify package, lưu ciphertext bằng PLAN 72, materialize ngắn hạn vào protected project workspace, rồi reuse local archive build/verify/export. Backend không nhận game path và không install output.

Ưu điểm: giữ commercial authority ở server, giảm casual harvesting, đồng thời giữ latency/privacy/local build của pipeline đã kiểm chứng. Existing project có valid workspace không bị phá khi entitlement hết hạn; acquisition mới vẫn cần online authorization.

Nhược điểm: plaintext vẫn tồn tại lúc ACV Tool chạy; authorized user có thể recover working/final archive. Cleanup/ACL/encryption chỉ là defense in depth.

## Quyết định

Chọn **C — Hybrid: server-controlled acquisition, client-side local build** cho V1.

Luồng bắt buộc:

```text
Authenticated desktop
  → server catalog + exact entitlement/grant
  → short-lived private package access
  → signature/length/SHA-256 verification
  → encrypted local cache
  → decrypt vào random protected workspace khi cần
  → existing Apply/Validate/Build/Pack/Verify
  → atomic standalone .ab/.acv export
  → END
```

Không có silent rebind sang version mới. Cache hit không cấp entitlement. Revocation/expiry chặn acquisition mới nhưng không remote-delete project, user edit hoặc output đã export. Pristine package không được bundle raw trong installer hay lưu ở global plaintext cache.

## Điều kiện pháp lý và release gate

- Legal owner xác nhận quyền phân phối, modification và redistribution cho từng `TemplateId + TemplateVersion`; record phải liên kết evidence ngoài source repository.
- Product copy/Terms không được tuyên bố template “không thể extract”. Người dùng được thông báo rằng final archive là file standalone.
- Production catalog/private storage/entitlement/nonce, TLS, audit/monitoring và incident response phải có evidence deployment.
- PLAN 74 workspace ACL/reparse/cleanup gates và PLAN 75 least-privilege review phải PASS.
- Concrete authenticated download → encrypted cache → workspace integration phải có end-to-end test trước commercial release.

## Hệ quả và residual risk

- Positive: không tạo server build dependency cho local editor; không upload project/user assets mặc định; reuse toàn bộ archive abstraction và atomic export.
- Negative: commercial template confidentiality không thể tuyệt đối; same-user malware/Administrator/process dump vẫn có thể lấy plaintext.
- Accepted residual: authorized user có thể extract final archive và unchanged assets. Đây là giới hạn sản phẩm/pháp lý cần quản lý, không phải bug có thể giải quyết chỉ bằng crypto client-side.
- Open deployment debt: concrete private storage/download adapter, live PostgreSQL/RLS/nonce test và production license evidence chưa verified.

## Trigger phải xem xét lại ADR

- License cấm giao pristine-derived bytes cho client.
- ACV Tool có server-compatible/sandboxed implementation được phê duyệt.
- Product chuyển sang online-only build bằng requirement explicit.
- Exposure incident chứng minh controls hybrid không đáp ứng risk appetite.

Mọi thay đổi lựa chọn cần ADR mới; không sửa lịch sử quyết định này để che thay đổi.
