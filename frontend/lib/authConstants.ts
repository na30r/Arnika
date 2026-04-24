/** httpOnly session cookie; HMAC in `sitemirror_token_sig` (readable by `fetch` + middleware). */
export const SESSION_COOKIE = "sitemirror_session";

export const PUBLIC_SIG_COOKIE = "sitemirror_token_sig";

/** When unset, use the same default as `SiteMirror.Api` appsettings.json `Auth:JwtSecret`. */
export const jwtSecret = () =>
  process.env.JWT_SECRET ?? "ChangeThisInProduction_UseALongRandomSecretStringAtLeast32";
export const jwtIssuer = () => process.env.JWT_ISSUER ?? "SiteMirror.Api";
export const jwtAudience = () => process.env.JWT_AUDIENCE ?? "SiteMirror.Clients";

export function normalizeJwtSecretBytes(secret: string): Uint8Array {
  const normalized = secret.length >= 32 ? secret : secret.padEnd(32, "x");
  return new TextEncoder().encode(normalized);
}
