import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";
import { jwtToUserPayload, verifyToken } from "./lib/auth-jwt";
import { PUBLIC_SIG_COOKIE, SESSION_COOKIE } from "./lib/authConstants";
import { hmacToken, timingSafeEqualStrings } from "./lib/sessionHmac";

const locales = ["en", "fa"] as const;
const defaultLocale = "en";
type AppLocale = (typeof locales)[number];

function isLocale(s: string): s is AppLocale {
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

function loginUrl(request: NextRequest, locale: string) {
  const u = new URL(`/${locale}/auth/login/`, request.url);
  const returnTo = request.nextUrl.pathname + request.nextUrl.search;
  u.searchParams.set("returnUrl", returnTo);
  return u;
}

function isAuthPage(pathname: string) {
  return /\/auth\/(login|register)\/?$/.test(pathname);
}

function firstLocaleFromPathname(pathname: string): string | null {
  const a = pathname.split("/").filter(Boolean)[0];
  return a && isLocale(a) ? a : null;
}

export async function middleware(request: NextRequest) {
  const { pathname } = request.nextUrl;
  if (pathname.startsWith("/_next") || pathname.startsWith("/api")) {
    return NextResponse.next();
  }
  // Other static assets (e.g. favicon at root) — not mirrored crawl files under `/mirror/`
  if (pathname.includes(".") && !pathname.startsWith("/mirror/") && pathname !== "/mirror") {
    return NextResponse.next();
  }

  if (pathname === "/mirror" || pathname.startsWith("/mirror/")) {
    if (!(await isAuthenticated(request))) {
      return NextResponse.redirect(loginUrl(request, defaultLocale));
    }
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

  const locale = firstLocaleFromPathname(pathname) ?? defaultLocale;
  if (isAuthPage(pathname)) {
    if (await isAuthenticated(request)) {
      const returnTo = request.nextUrl.searchParams.get("returnUrl");
      if (returnTo && returnTo.startsWith("/") && !returnTo.startsWith("//")) {
        return NextResponse.redirect(new URL(returnTo, request.url));
      }
      return NextResponse.redirect(new URL(`/${locale}/`, request.url));
    }
    return NextResponse.next();
  }

  if (!(await isAuthenticated(request))) {
    return NextResponse.redirect(loginUrl(request, locale));
  }
  return NextResponse.next();
}

export const config = {
  matcher: ["/((?!_next/static|_next/image|favicon.ico).*)"]
};
