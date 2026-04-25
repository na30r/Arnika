import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";

const locales = ["en", "fa"];
const defaultLocale = "en";

function isMirrorHtmlPath(pathname: string): boolean {
  return /^\/mirror\/.+\.html\/?$/i.test(pathname);
}

export function middleware(request: NextRequest) {
  const { pathname } = request.nextUrl;
  if (isMirrorHtmlPath(pathname)) {
    const normalizedMirrorPath = pathname.endsWith("/") ? pathname.slice(0, -1) : pathname;
    const rewritten = request.nextUrl.clone();
    rewritten.pathname = "/api/mirror-page";
    rewritten.searchParams.set("path", normalizedMirrorPath);
    return NextResponse.rewrite(rewritten);
  }

  if (
    pathname.startsWith("/_next") ||
    pathname.startsWith("/api") ||
    pathname.startsWith("/mirror") ||
    pathname.includes(".")
  ) {
    return NextResponse.next();
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
