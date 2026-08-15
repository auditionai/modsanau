# Trạm Sáng Tạo: model tạo ảnh

AI Studio chỉ nhận model có `type=image` và nằm trong allowlist của Function `tst-image`. Catalog được đọc từ `GET https://api.tramsangtao.com/v1/models`, sau đó lọc server-side; model video, motion, KOL, TTS và LLM không được trả về desktop.

Các model ảnh hiện được allowlist: `flux-2-pro`, `grok-image`, `image-4.0`, `image-gpt`, `image-gpt-2`, `imagen-4`, `imagen-4-fast`, `imagen-4-ultra`, `kling-o1-image`, `nano-banana`, `nano-banana-2`, `nano-banana-pro`, `nano-banana-pro-cheap`, `seedream-4.5`, `seedream-5-pro`.

Settings không tự phát minh. Function trả nguyên `servers`, `pricing[]`, `modes`, `params` và `notes` của từng model từ `/models`. Client chỉ hiển thị field có trong `params`; chi phí lấy từ row `pricing[]` khớp với settings gửi lên. `image-gpt-2` hỗ trợ `quality=low|medium|high`; các model còn lại phải tuân contract live của `/models`.

## Thiết lập bằng Supabase Web

Không dùng Supabase CLI hoặc Supabase Local cho quy trình này. Thực hiện hoàn toàn trên Supabase Dashboard Web của project `plvuutsjwsawkkrmvigz`:

1. Mở Supabase Dashboard và chọn project `plvuutsjwsawkkrmvigz`.
2. Vào **Edge Functions**, chọn **Deploy a new function**, đặt tên `tst-image`.
3. Dán nội dung file `supabase/functions/tst-image/index.ts` vào editor của Function và chọn **Deploy function**.
4. Vào **Project Settings** → **Edge Functions** → **Secrets**.
5. Chọn **Add new secret**, nhập tên `TST_API_KEY`, dán API key TST vào phần value rồi lưu.
6. Quay lại Function `tst-image`, mở tab **Details/Invocations** để xác nhận Function đã deploy. Không ghi hoặc chụp lại giá trị secret trong log.

Supabase tự cung cấp `SUPABASE_URL` và `SUPABASE_ANON_KEY` cho Edge Function. Không tạo thêm hai secret này và không đưa `TST_API_KEY` vào desktop, GitHub, Netlify hoặc biến môi trường public.

## Kiểm tra trên Supabase Web

Trong trang Function `tst-image`, dùng công cụ **Test** với access token của một user đã đăng nhập:

- Method: `GET`
- Query: `action=models`
- Header: `Authorization: Bearer <SUPABASE_USER_ACCESS_TOKEN>`

Kết quả hợp lệ chỉ có `type: "image"` và các model nằm trong allowlist ở trên. Nếu chưa thêm `TST_API_KEY`, Function trả `TST_API_KEY_NOT_CONFIGURED` mà không lộ giá trị key.

Desktop gọi Function bằng Supabase access token của user:

`POST /functions/v1/tst-image?action=generate`

`GET /functions/v1/tst-image?action=models`

`GET /functions/v1/tst-image?action=status&job_id=...`

Function dùng `Authorization: Bearer <TST_API_KEY>` chỉ ở server khi gọi provider.
