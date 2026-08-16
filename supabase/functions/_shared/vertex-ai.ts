type VertexCredentials = {
  type: string;
  project_id: string;
  client_email: string;
  private_key: string;
  token_uri?: string;
};

type VertexConfiguration = {
  credentialsJson: string;
  projectId: string;
  region: string;
  modelId: string;
};

const encoder = new TextEncoder();
const VERTEX_MODEL_FALLBACK = "gemini-3.1-flash";

function base64Url(bytes: Uint8Array) {
  let text = "";
  for (const byte of bytes) text += String.fromCharCode(byte);
  return btoa(text).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
}

function pemToBytes(pem: string) {
  const normalized = pem.replace(/-----BEGIN PRIVATE KEY-----|-----END PRIVATE KEY-----|\s/g, "");
  const text = atob(normalized);
  return Uint8Array.from(text, (char) => char.charCodeAt(0));
}

async function accessToken(credentials: VertexCredentials) {
  const now = Math.floor(Date.now() / 1000);
  const header = base64Url(encoder.encode(JSON.stringify({ alg: "RS256", typ: "JWT" })));
  const claims = base64Url(encoder.encode(JSON.stringify({
    iss: credentials.client_email,
    scope: "https://www.googleapis.com/auth/cloud-platform",
    aud: credentials.token_uri ?? "https://oauth2.googleapis.com/token",
    iat: now,
    exp: now + 3300,
  })));
  const key = await crypto.subtle.importKey("pkcs8", pemToBytes(credentials.private_key),
    { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["sign"]);
  const signature = await crypto.subtle.sign("RSASSA-PKCS1-v1_5", key, encoder.encode(`${header}.${claims}`));
  const assertion = `${header}.${claims}.${base64Url(new Uint8Array(signature))}`;
  const response = await fetch(credentials.token_uri ?? "https://oauth2.googleapis.com/token", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "urn:ietf:params:oauth:grant-type:jwt-bearer", assertion }),
  });
  const body = await response.json().catch(() => ({}));
  if (!response.ok || typeof body.access_token !== "string") throw new Error("VERTEX_TOKEN_FAILED");
  return body.access_token;
}

export async function composeWithVertex(
  configuration: VertexConfiguration,
  brief: string,
  referenceImages: string[],
) {
  const credentials = JSON.parse(configuration.credentialsJson) as VertexCredentials;
  if (credentials.type !== "service_account" || credentials.project_id !== configuration.projectId
    || !credentials.client_email || !credentials.private_key) throw new Error("VERTEX_CREDENTIALS_INVALID");
  const token = await accessToken(credentials);
  const parts: Array<Record<string, unknown>> = [{ text: brief }];
  for (const dataUrl of referenceImages) {
    const base64 = dataUrl.slice("data:image/png;base64,".length);
    parts.push({ inlineData: { mimeType: "image/png", data: base64 } });
  }
  const modelIds = [...new Set([configuration.modelId, VERTEX_MODEL_FALLBACK])];
  for (const modelId of modelIds) {
    const response = await fetch(
      `https://${configuration.region}-aiplatform.googleapis.com/v1/projects/${encodeURIComponent(configuration.projectId)}/locations/${encodeURIComponent(configuration.region)}/publishers/google/models/${encodeURIComponent(modelId)}:generateContent`,
      {
        method: "POST",
        headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
        body: JSON.stringify({
          systemInstruction: { parts: [{ text: "You are an image-prompt orchestrator. Return only one detailed English prompt for an image generator. Preserve requested layout constraints, use supplied images only as visual references, and do not mention policy or your process." }] },
          contents: [{ role: "user", parts }],
          generationConfig: { temperature: 0.35, maxOutputTokens: 1200 },
        }),
      },
    );
    const body = await response.json().catch(() => ({}));
    const prompt = body?.candidates?.[0]?.content?.parts?.map((part: { text?: unknown }) => part.text)
      .filter((text: unknown): text is string => typeof text === "string").join(" ").trim();
    if (response.ok && prompt && prompt.length <= 16000) return prompt;
    if (response.status !== 404 || modelId === VERTEX_MODEL_FALLBACK) break;
  }
  throw new Error("VERTEX_COMPOSITION_FAILED");
}
