import type { NextRequest } from "next/server";
import { jwtToUserPayload, verifyToken } from "./auth-jwt";
import { validCookiePair } from "./auth-session";
import { hmacToken } from "./sessionHmac";
import { SESSION_COOKIE } from "./authConstants";

/**
 * Resolves the JWT and user for API route handlers: httpOnly session + sig cookie, or
 * `Authorization: Bearer` (full JWT or HMAC that pairs with the httpOnly session).
 */
export async function getAuthFromRequest(request: NextRequest) {
  const fromCookie = await validCookiePair(request);
  if (fromCookie) {
    try {
      const payload = await verifyToken(fromCookie);
      const user = jwtToUserPayload(payload);
      if (user) {
        return { token: fromCookie, user };
      }
    } catch {
      /* try header */
    }
  }

  const auth = request.headers.get("authorization");
  if (auth?.startsWith("Bearer ")) {
    const value = auth.slice(7);
    const session = request.cookies.get(SESSION_COOKIE)?.value;
    if (session) {
      const h = await hmacToken(session);
      if (h === value) {
        try {
          const payload = await verifyToken(session);
          const user = jwtToUserPayload(payload);
          if (user) {
            return { token: session, user };
          }
        } catch {
          return null;
        }
      }
    }
    try {
      const payload = await verifyToken(value);
      const user = jwtToUserPayload(payload);
      if (user) {
        return { token: value, user };
      }
    } catch {
      return null;
    }
  }
  return null;
}

export function authHeaderForBackend(token: string) {
  return { Authorization: `Bearer ${token}` };
}
