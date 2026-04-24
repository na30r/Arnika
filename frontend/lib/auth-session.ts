import { decodeJwt } from "jose";
import { cookies } from "next/headers";
import type { NextRequest } from "next/server";
import { NextResponse } from "next/server";
import { jwtToUserPayload, verifyToken } from "./auth-jwt";
import type { UserPayload } from "./auth";
import { PUBLIC_SIG_COOKIE, SESSION_COOKIE } from "./authConstants";
import { hmacToken, timingSafeEqualStrings } from "./sessionHmac";

export { hmacToken as hmacForSessionToken } from "./sessionHmac";

export { verifyToken, jwtToUserPayload } from "./auth-jwt";

export function sessionCookieOptions() {
  return {
    name: SESSION_COOKIE,
    path: "/",
    httpOnly: true as const,
    sameSite: "lax" as const,
    secure: process.env.NODE_ENV === "production"
  };
}

export async function setSessionResponse(token: string): Promise<NextResponse> {
  const payload = decodeJwt(token) as { exp?: number };
  const maxAge = payload.exp
    ? Math.max(0, payload.exp - Math.floor(Date.now() / 1000))
    : 60 * 24 * 60 * 60;
  const sig = await hmacToken(token);
  const res = NextResponse.json({ ok: true });
  const base = sessionCookieOptions();
  res.cookies.set(base.name, token, {
    ...base,
    maxAge
  });
  res.cookies.set(PUBLIC_SIG_COOKIE, sig, {
    path: "/",
    httpOnly: false,
    sameSite: "lax",
    secure: process.env.NODE_ENV === "production",
    maxAge
  });
  return res;
}

export function clearSessionResponse(): NextResponse {
  const res = NextResponse.json({ ok: true });
  const base = sessionCookieOptions();
  res.cookies.set(base.name, "", { ...base, maxAge: 0 });
  res.cookies.set(PUBLIC_SIG_COOKIE, "", { path: "/", maxAge: 0 });
  return res;
}

export async function validCookiePair(request: NextRequest): Promise<string | null> {
  const session = request.cookies.get(SESSION_COOKIE)?.value;
  if (!session) {
    return null;
  }
  const sig = request.cookies.get(PUBLIC_SIG_COOKIE)?.value;
  if (!sig) {
    return null;
  }
  const expected = await hmacToken(session);
  if (!timingSafeEqualStrings(sig, expected)) {
    return null;
  }
  return session;
}

/**
 * Resolves the signed-in user from the session cookie in Server Components / `cookies()`.
 */
export async function getServerUser(): Promise<UserPayload | null> {
  const c = await cookies();
  const token = c.get(SESSION_COOKIE)?.value;
  if (!token) {
    return null;
  }
  const sig = c.get(PUBLIC_SIG_COOKIE)?.value;
  if (!sig) {
    return null;
  }
  if (!timingSafeEqualStrings(sig, await hmacToken(token))) {
    return null;
  }
  try {
    const payload = await verifyToken(token);
    return jwtToUserPayload(payload);
  } catch {
    return null;
  }
}
