import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";
import { jwtToUserPayload, verifyToken } from "./lib/auth-jwt";
import { PUBLIC_SIG_COOKIE, SESSION_COOKIE } from "./lib/authConstants";
import { hmacToken, timingSafeEqualStrings } from "./lib/sessionHmac";

const locales = ["en", "fa"] as const;
const defaultLocale = "en";

function isLocale(s: string): s is (typeof locales)[number] {
  return (locales as readonly string[]).includes(s);
}

async function isAuthenticated(request: NextRequest): Promise<boolean> {
  const session = request.cookies.get(SESSION_COOKIE)?.value;
  if (!session) {
    return false;
  }
  const sig = request.cookies.get(PUBLIC_SIG_COOKIE)?.value;
  if (!sig) {
    return false;
  }
  const expected = await hmacToken(session);
  if (!timingSafeEqualStrings(sig, expected)) {
    return false;
  }
  try {
    const payload = await verifyToken(session);
    return jwtToUserPayload(payload) != null;
  } catch {
    return false;
  }
}

function isAuthPage(pathname: string) {
  return /\/auth\/(login|register)\/?$/.test(pathname);
}

function firstLocaleFromPathname(pathname: string): string | null {
  const a = pathname.split("/").filter(Boolean)[0];
  return a && isLocale(a) ? a : null;
}

/**
 * Locale prefixing only. No global auth redirect: all pages and `/mirror/...` static
 * files are public; sign-in is optional (navbar + API still enforce for protected calls).
 */
export async function middleware(request: NextRequest) {
  const { pathname } = request.nextUrl;
  if (pathname.startsWith("/_next") || pathname.startsWith("/api")) {
    return NextResponse.next();
  }
  if (pathname.includes(".") && !pathname.startsWith("/mirror/") && pathname !== "/mirror") {
    return NextResponse.next();
  }

  const hasLocale = locales.some(
    (l) => pathname === `/${l}` || pathname === `/${l}/` || pathname.startsWith(`/${l}/`)
  );
  if (!hasLocale) {
    if (pathname === "" || pathname === "/") {
      return NextResponse.redirect(new URL(`/${defaultLocale}/`, request.url));
    }
    return NextResponse.redirect(new URL(`/${defaultLocale}${pathname}/`, request.url));
  }

  if (isAuthPage(pathname) && (await isAuthenticated(request))) {
    const locale = firstLocaleFromPathname(pathname) ?? defaultLocale;
    const returnTo = request.nextUrl.searchParams.get("returnUrl");
    if (returnTo && returnTo.startsWith("/") && !returnTo.startsWith("//")) {
      return NextResponse.redirect(new URL(returnTo, request.url));
    }
    return NextResponse.redirect(new URL(`/${locale}/`, request.url));
  }

  return NextResponse.next();
}

export const config = {
  matcher: ["/((?!_next/static|_next/image|favicon.ico).*)"]
};
