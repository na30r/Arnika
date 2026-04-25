"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { localePath } from "../lib/appPath";
import { type Locale, t, locales } from "../lib/i18n";
import { clearSession, getStoredUser } from "../lib/auth";

type Props = {
  locale: Locale;
};

export function Navbar({ locale }: Props) {
  const pathname = usePathname();
  const user = getStoredUser();

  function switchLocale(next: Locale) {
    const segments = pathname.split("/").filter(Boolean);
    if (segments.length === 0) {
      window.location.href = `/${next}`;
      return;
    }
    if (locales.includes(segments[0] as Locale)) {
      segments[0] = next;
    } else {
      segments.unshift(next);
    }
    window.location.href = `/${segments.join("/")}`;
  }

  function signOut() {
    clearSession();
    window.location.href = localePath(locale, "");
  }

  const home = localePath(locale, "");
  return (
    <header className="site-nav">
      <div className="nav-inner">
        <Link href={home} className="nav-brand">
          Web Mirror
        </Link>
        <nav className="nav-links">
          <Link href={home}>{t("nav.home", locale)}</Link>
          <Link href={localePath(locale, "profile")}>{t("nav.profile", locale)}</Link>
        </nav>
        <div className="nav-actions">
          <label className="nav-lang">
            <span className="sr-only">{t("nav.lang", locale)}</span>
            <select
              value={locale}
              onChange={(e) => switchLocale(e.target.value as Locale)}
              aria-label={t("nav.lang", locale)}
            >
              {locales.map((l) => (
                <option key={l} value={l}>
                  {l.toUpperCase()}
                </option>
              ))}
            </select>
          </label>
          {user ? (
            <>
              <span className="nav-user">{user.userName}</span>
              <button type="button" className="btn-ghost" onClick={signOut}>
                {t("nav.signOut", locale)}
              </button>
            </>
          ) : (
            <>
              <Link href={localePath(locale, "auth/login")} className="btn-ghost">
                {t("nav.signIn", locale)}
              </Link>
              <Link href={localePath(locale, "auth/register")} className="btn-primary small">
                {t("nav.signUp", locale)}
              </Link>
            </>
          )}
        </div>
      </div>
    </header>
  );
}
