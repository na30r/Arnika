import { jwtSecret } from "./authConstants";

const enc = new TextEncoder();

function hmacKey(): string {
  const s = process.env.SESSION_HMAC_SECRET ?? "";
  return s.length >= 8 ? s : jwtSecret();
}

/**
 * HMAC-SHA256 in base64url, matching server cookie signing. Works in Node and Edge (Web Crypto).
 */
export async function hmacToken(token: string): Promise<string> {
  const key = await crypto.subtle.importKey(
    "raw",
    enc.encode(hmacKey()),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"]
  );
  const sig = await crypto.subtle.sign("HMAC", key, enc.encode(token));
  const bytes = new Uint8Array(sig);
  let binary = "";
  for (let i = 0; i < bytes.length; i += 1) {
    binary += String.fromCharCode(bytes[i]!);
  }
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function timingSafeEqualStrings(a: string, b: string): boolean {
  if (a.length !== b.length) {
    return false;
  }
  let diff = 0;
  for (let i = 0; i < a.length; i += 1) {
    diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  }
  return diff === 0;
}
