import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";

const locales = ["en", "fa"];
const defaultLocale = "en";

export function middleware(request: NextRequest) {
  const { pathname } = request.nextUrl;

  if (pathname.startsWith("/_next") || pathname.startsWith("/api")) {
    return NextResponse.next();
  }

  // Common root static files (no locale prefix in public/)
  if (pathname === "/favicon.ico" || pathname === "/robots.txt" || pathname === "/sitemap.xml") {
    return NextResponse.next();
  }

  // Static mirror output lives at /public/mirror/... but we show it inside the app shell with nav at /[locale]/mirror/...
  if (
    (pathname === "/mirror" || pathname.startsWith("/mirror/")) &&
    !/\/(en|fa)\/mirror(\/|$)/.test(pathname)
  ) {
    const suffix = pathname === "/mirror" || pathname === "/mirror/" ? "/" : pathname.slice("/mirror".length);
    const target = new URL(`/${defaultLocale}/mirror${suffix}`, request.url);
    target.search = request.nextUrl.search;
    return NextResponse.redirect(target);
  }

  const hasLocale = locales.some(
    (l) => pathname === `/${l}` || pathname.startsWith(`/${l}/`)
  );
  if (hasLocale) {
    return NextResponse.next();
  }

  if (pathname === "" || pathname === "/") {
    return NextResponse.redirect(new URL(`/${defaultLocale}`, request.url));
  }

  return NextResponse.redirect(new URL(`/${defaultLocale}${pathname}`, request.url));
}

export const config = {
  matcher: ["/((?!_next/static|_next/image|favicon.ico).*)"]
};
