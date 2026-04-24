import { jwtVerify } from "jose";
import type { UserPayload } from "./auth";
import { jwtAudience, jwtIssuer, jwtSecret, normalizeJwtSecretBytes } from "./authConstants";

type JwtPayload = {
  sub?: string;
  name?: string;
  unique_name?: string;
  [key: string]: unknown;
};

/** Verifies a JWT (same HMAC/issuer/audience as SiteMirror.Api). Edge-safe. */
export async function verifyToken(token: string) {
  const key = normalizeJwtSecretBytes(jwtSecret());
  const { payload } = await jwtVerify(token, key, {
    issuer: jwtIssuer(),
    audience: jwtAudience()
  });
  return payload as JwtPayload;
}

export function jwtToUserPayload(payload: JwtPayload): UserPayload | null {
  const userId = typeof payload.sub === "string" ? payload.sub : null;
  const userName =
    (typeof payload.unique_name === "string" && payload.unique_name) ||
    (typeof payload.name === "string" && payload.name) ||
    null;
  if (!userId || !userName) {
    return null;
  }
  return {
    userId,
    userName,
    phoneNumber: null,
    subscriptionEndDateUtc: null
  };
}
