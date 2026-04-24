import { notFound } from "next/navigation";
import { AuthNavbar } from "@/components/auth-navbar";
import { LocaleShell } from "../../components/LocaleShell";
import { SessionProvider } from "../../components/SessionContext";
import { getServerUser } from "../../lib/auth-session";
import { defaultLocale, locales, type Locale } from "../../lib/i18n";

type Props = {
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
};

/**
 * All locale routes: i18n shell, global `AuthNavbar` (UX), and session for the nav.
 * Server auth is enforced in `middleware.ts`.
 */
export default async function LocaleLayout({ children, params }: Props) {
  const { locale: raw } = await params;
  if (!locales.includes(raw as Locale)) {
    notFound();
  }
  const locale = (raw as Locale) || defaultLocale;
  const serverUser = await getServerUser();
  return (
    <SessionProvider initialUser={serverUser}>
      <LocaleShell locale={locale}>
        <AuthNavbar locale={locale} />
        {children}
      </LocaleShell>
    </SessionProvider>
  );
}
