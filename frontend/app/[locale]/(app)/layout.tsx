import { Navbar } from "../../../components/Navbar";
import { defaultLocale, locales, type Locale } from "../../../lib/i18n";
import { notFound } from "next/navigation";

type Props = {
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
};

/**
 * App shell: main mirror UI and profile (with navbar + language on profile).
 * Login/register use the parent [locale] layout only (no navbar).
 */
export default async function AppLayout({ children, params }: Props) {
  const { locale: raw } = await params;
  if (!locales.includes(raw as Locale)) {
    notFound();
  }
  const locale = (raw as Locale) || defaultLocale;
  return (
    <>
      <Navbar locale={locale} />
      {children}
    </>
  );
}
