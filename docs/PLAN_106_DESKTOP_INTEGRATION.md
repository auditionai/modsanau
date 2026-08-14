# PLAN 106: Desktop App Integration Guide

## Current Status

✅ **Completed:**
- Supabase Edge Function deployed with JWT authentication
- Edge Function proxies to Trạm Sáng Tạo API
- API key secured server-side only
- Successfully tested: balance check, image generation, job polling

⏳ **Pending:**
- Desktop App integration code

## Architecture Overview

```
Desktop App (WinUI)
  ↓ Supabase JWT token
Edge Function (Supabase)
  ↓ Trạm Sáng Tạo API key (server-side)
Trạm Sáng Tạo API
```

## Edge Function Endpoints

**Base URL:** `https://plvuutsjwsawkkrmvigz.supabase.co/functions/v1/ai-proxy`

### Authentication
All requests require Supabase user JWT token:
```
Authorization: Bearer <user_jwt_token>
```

### Endpoints

#### 1. Generate Image
```http
POST /ai-proxy/generate
Content-Type: application/json

{
  "prompt": "A beautiful landscape",
  "width": 1024,
  "height": 768,
  "num_outputs": 1
}
```

Response:
```json
{
  "job_id": "c250bab3-ca00-42ff-89e0-48ab041555fc",
  "status": "pending"
}
```

#### 2. Check Job Status
```http
GET /ai-proxy/jobs/{job_id}
```

Response:
```json
{
  "id": "c250bab3-ca00-42ff-89e0-48ab041555fc",
  "status": "completed",
  "result_url": "https://cdn.tramsangtao.com/outputs/.../result.png"
}
```

Status values: `pending`, `processing`, `completed`, `failed`

#### 3. Check Balance
```http
GET /ai-proxy/balance
```

Response:
```json
{
  "credits": 3746
}
```

#### 4. Upscale Image
```http
POST /ai-proxy/upscale
Content-Type: application/json

{
  "image": "https://...",
  "scale": 2
}
```

## Integration Options

### Option 1: Create New EdgeFunctionAiStudioService

Create `src/AuditionModStudio.Cloud/EdgeFunctionAiStudioService.cs` that implements `IAiStudioService` and calls Edge Function endpoints directly.

**Pros:**
- Clean separation from old Gateway code
- Simpler API, fewer round-trips
- Direct CDN URLs for results

**Cons:**
- Need to implement new service from scratch
- Different error handling patterns

### Option 2: Update Edge Function to Match Gateway API

Modify Edge Function to provide Gateway-compatible endpoints:
- `POST v1/ai/jobs` - Submit job
- `GET v1/ai/jobs/{jobId}` - Poll status
- `POST v1/ai/content/{kind}` - Upload content
- `GET v1/ai/content/{contentId}` - Download content

**Pros:**
- Minimal Desktop App code changes
- Reuse existing `GatewayAiStudioService`

**Cons:**
- More complex Edge Function
- Extra content upload/download steps

## Recommended Approach: Option 1

Create new service because:
1. Edge Function API is simpler and more efficient
2. Clean break from old Gateway architecture
3. Direct CDN URLs eliminate content proxy overhead

## Implementation Steps

### 1. Create EdgeFunctionAiStudioService

```csharp
public sealed class EdgeFunctionAiStudioService(
    HttpClient httpClient,
    ISecureSessionStore sessionStore,
    IAuthenticationService authenticationService,
    EdgeFunctionOptions options,
    IImageImportService imageImport) : IAiStudioService
{
    // Implement IAiStudioService methods
}
```

### 2. Update ApplicationBootstrapper.cs

Replace `GatewayAiStudioService` registration (line 239-259) with:

```csharp
builder.Services.AddSingleton<IAiStudioService>(services =>
{
    var (url, publishableKey) = ProductionSupabaseConfiguration.Resolve();
    if (!Uri.TryCreate(url, UriKind.Absolute, out var projectUri))
    {
        return new UnavailableAiStudioService();
    }
    
    // Edge Function URL: {supabase_url}/functions/v1/ai-proxy
    var edgeFunctionUri = new Uri(projectUri, "functions/v1/ai-proxy");
    var options = new EdgeFunctionOptions(edgeFunctionUri);
    
    return options.IsValid
        ? new EdgeFunctionAiStudioService(
            services.GetRequiredService<HttpClient>(),
            services.GetRequiredService<ISecureSessionStore>(),
            services.GetRequiredService<IAuthenticationService>(),
            options,
            services.GetRequiredService<IImageImportService>())
        : new UnavailableAiStudioService();
});
```

### 3. Environment Variables

**Remove old Gateway variables:**
- ~~`AUDITION_GATEWAY_URL`~~
- ~~`AUDITION_DEVICE_SESSIONS_ENABLED`~~

**Supabase variables already configured:**
- Handled by `ProductionSupabaseConfiguration.Resolve()`
- Returns: `https://plvuutsjwsawkkrmvigz.supabase.co`

### 4. Key Differences from Gateway

| Aspect | Old Gateway | New Edge Function |
|--------|-------------|-------------------|
| Content Upload | Separate endpoint | Base64 in request |
| Job Polling | Gateway job ID | Trạm Sáng Tạo job ID |
| Result Download | Gateway content proxy | Direct CDN URL |
| Authentication | JWT + Device Session | JWT only |

### 5. Error Handling

Edge Function returns standard HTTP status codes:
- `401 Unauthorized` - Invalid/expired JWT
- `404 Not Found` - Invalid endpoint
- `500 Internal Server Error` - Trạm Sáng Tạo API error

### 6. Testing Checklist

- [ ] User login and JWT token acquisition
- [ ] Balance check endpoint
- [ ] Image generation submission
- [ ] Job status polling
- [ ] Result download from CDN
- [ ] Error scenarios (expired JWT, API failure)
- [ ] Multiple concurrent jobs

## Security Notes

- **API Key Storage:** NEVER store Trạm Sáng Tạo API key in Desktop App, source code, or config files
- **JWT Token:** Desktop App sends user JWT, Edge Function validates it
- **Supabase Secrets:** API key lives in `TRAM_SANG_TAO_API_KEY` environment variable server-side only
- **CDN URLs:** Public, no authentication required (signed URLs from Trạm Sáng Tạo)

## Reference Implementation

See test script: `test-edge-function.ps1` for working example of API calls.

## Next Steps

1. Create `EdgeFunctionAiStudioService.cs`
2. Update `ApplicationBootstrapper.cs`
3. Remove old Gateway environment variables
4. Run integration tests
5. Update Desktop App documentation
