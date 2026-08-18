# GPTi2 image models

AI Studio only exposes the GPTi2 image models `gpt-image-2` and `nano-banana-pro`.
The desktop client calls the trusted Supabase Edge Function `gpti2-image`; it never
stores or sends the GPTi2 API key directly.

Set the provider credential only as a Supabase secret:

```powershell
supabase secrets set GPTI2_API_KEY="sk-..." --project-ref plvuutsjwsawkkrmvigz
```

Deploy the function with:

```powershell
supabase functions deploy gpti2-image --project-ref plvuutsjwsawkkrmvigz --no-verify-jwt
```

`gpt-image-2` supports the documented GPTi2 24 pixel sizes, `low`/`medium`/`high`
quality and `n` from 1 to 4. The application obtains the available settings from the
trusted backend and internal Credit pricing remains server-authoritative.
