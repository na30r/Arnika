import type { Locale } from "./i18n";

/** Match Next `trailingSlash: true` — use for client redirects and `window.location`. */
export function localeHome(locale: Locale): string {
  return `/${locale}/`;
}

export function localePath(locale: Locale, path: string): string {
  const clean = path.replace(/^\/+|\/+$/g, "");
  return clean ? `/${locale}/${clean}/` : `/${locale}/`;
}
