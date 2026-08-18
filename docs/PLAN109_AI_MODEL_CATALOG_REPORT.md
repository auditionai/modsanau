# PLAN 109 - Catalog Model AI Va Bang Gia Noi Bo

## Muc tieu

- Dong bo cac model anh do TST tra ve, khong khoa vao danh sach Nano Banana.
- Gia Credits do server cua Audition AI Mod Studio quyet dinh, khong lay tu `pricing` cua TST.
- Admin co the thay doi gia, bat/tat model va de lai audit record.

## Thay doi

- Them `private.ai_model_catalog` va RPC `public.ai_model_catalog_api`.
- `tst-image` chi nhan model co phan loai image, dong bo catalog, va lay gia hien hanh tu catalog truoc khi reserve Credits.
- Them Edge Function `ai-model-admin` de admin doc va cap nhat catalog voi xac thuc, MFA va recent-auth.
- Them man hinh `Model AI & gia` trong admin portal.

## Bao mat

- Client khong gui hoac quyet dinh Credits.
- Model bi tat khong duoc tra ve cho AI Studio va job moi bi tu choi khi catalog khong co gia hop le.
- Thay doi gia can admin active, MFA verified, dang nhap gan day va ly do toi thieu 8 ky tu.
- TST API key van chi nam trong Edge Function.

## Kiem thu

- `deno check --config supabase/functions/tst-image/deno.json supabase/functions/tst-image/index.ts`: PASS.
- `deno check --config supabase/functions/ai-model-admin/deno.json supabase/functions/ai-model-admin/index.ts`: PASS.
- `npm test`: PASS.
- `dotnet test tests/Gateway.Tests/Gateway.Tests.csproj --no-restore`: PASS, 146 passed, 12 skipped do khong co Postgres integration environment.

## Gioi han

- Chua chay migration hoac deploy Edge Function vao Supabase production trong PLAN nay.
- Catalog TST duoc dong bo khi AI Studio tai danh sach model; model moi nhan gia mac dinh 10 Credits cho den khi admin dat gia rieng.
