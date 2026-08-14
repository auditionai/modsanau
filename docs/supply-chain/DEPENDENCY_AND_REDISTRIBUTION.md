# Dependency, provenance và redistribution policy — PLAN 88

## Dependency và lock policy

- SDK pin tại `global.json` là .NET SDK 10.0.302; GitHub Actions cũng dùng exact SDK này.
- NuGet version pin tập trung trong `Directory.Packages.props`; project không đặt version riêng hoặc floating range.
- Mọi project có `packages.lock.json`. Restore CI bắt buộc `--locked-mode`; update dependency phải sửa central version,
  restore có chủ ý, review lock diff, chạy audit/test rồi regenerate SBOM.
- `NuGet.Config` chỉ cho nguồn HTTPS NuGet.org, package-source mapping `*` và yêu cầu package signature validation.
- `NuGetAuditMode=all`; `Invoke-DependencyAudit.ps1` fail nếu direct hoặc transitive package có vulnerability do source hiện
  tại báo cáo. Audit phụ thuộc availability/freshness của advisory feed và không chứng minh package không có zero-day.
- GitHub Actions dependency được pin bằng full commit SHA, không dùng mutable tag làm execution authority.

## SBOM và license

`New-DependencySbom.ps1` tạo SPDX 2.3 JSON deterministic từ exact resolved graph. Nó hợp nhất package/version của toàn
solution, phân biệt direct/transitive, thêm NuGet purl/download location và đọc SPDX license expression từ local `.nuspec`.
`NOASSERTION` có nghĩa metadata package chưa đủ, không có nghĩa public domain hoặc được phép phân phối tùy ý. CI dùng
`-Verify` để fail khi graph và tracked SBOM khác nhau.

Inventory hiện có 68 package-version duy nhất: 42 khai báo MIT, 10 Apache-2.0, 1 PostgreSQL và 15 `NOASSERTION`.
Nhóm `NOASSERTION` gồm WebView2, Windows SDK BuildTools/MSIX, Windows App SDK cùng các component AI/Base/DWrite/
Foundation/InteractiveExperiences/ML/Runtime/Widgets/WinUI và `xunit.abstractions`. Trước commercial release phải đối
chiếu notice/license từ exact package payload hoặc tài liệu publisher; SBOM không tự chuyển nhóm này thành approved.

SBOM NuGet không tự bao phủ .NET runtime/Windows system component, native payload nằm trong package, tool ngoài NuGet,
proprietary template hay output người dùng. Release engineering phải ghép SBOM này với inventory file của exact signed
publish artifact trước phát hành. License notice của binary/package thực tế vẫn phải được giữ theo điều khoản tương ứng.

## Native/proprietary provenance

Machine-readable source of truth là `THIRD_PARTY_PROVENANCE.json`.

- DirectXTex `texconv.exe` May 2026 x64: local file version 2026.5.8.1, exact SHA-256 đã pin và Authenticode Microsoft hợp
  lệ. Official May 2026 release/tag được ký/verified và project công bố MIT. Đây là technical provenance cho exact local
  candidate, không thay release-package review. Khi redistribute phải kèm MIT notice và verify lại exact artifact.
- ACV Tool 5: exact local hash đã biết nhưng binary không Authenticode, không có version/publisher metadata, source và
  license không có authoritative evidence trong repository. Trạng thái thương mại là `BLOCKED_PENDING_WRITTEN_RIGHTS_AND_PROVENANCE_APPROVAL`.
- `015.ab`, mọi pristine template và extracted/game asset: hash chỉ là integrity identity. Ownership/license/redistribution
  rights chưa có authoritative evidence; commercial distribution bị chặn.
- `015.keydat` là runtime companion artifact, không phải secret/DRM, nhưng điều đó không tự cấp quyền redistribute.

`Test-SupplyChainPolicy.ps1` kiểm lock coverage, policy record, Action SHA và bảo đảm proprietary fixture không bị track.
Không script nào tải, bundle hoặc cấp phép ACV/template/game content.

## Commercial release gate

Trước commercial distribution phải có owner/pháp lý cung cấp bằng chứng bằng văn bản cho từng proprietary artifact:
chủ sở hữu/licensor, phạm vi quyền, lãnh thổ, thời hạn, quyền sửa đổi/sublicense/redistribute, nghĩa vụ notice và quy trình
thu hồi. Release manifest phải bind exact version/hash/source/signature/SBOM/license notices vào signed package provenance.
Thiếu bất kỳ approval/license closure nào thì build nội bộ có thể tiếp tục với fixture ngoài Git, nhưng package thương mại
không được chứa artifact đó. Trạng thái hiện tại:
`SUPPLY-CHAIN CONTROLS LOCALLY VERIFIED / COMMERCIAL LICENSE REVIEW OPEN / PROPRIETARY COMMERCIAL REDISTRIBUTION BLOCKED`.
