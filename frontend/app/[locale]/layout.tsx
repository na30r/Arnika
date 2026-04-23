import { notFound } from "next/navigation";
import { Navbar } from "../../components/Navbar";
import { LocaleShell } from "../../components/LocaleShell";
import { defaultLocale, locales, type Locale } from "../../lib/i18n";

type Props = {
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
};

export default async function LocaleLayout({ children, params }: Props) {
  const { locale: raw } = await params;
  if (!locales.includes(raw as Locale)) {
    notFound();
  }
  const locale = (raw as Locale) || defaultLocale;
  return (
    <LocaleShell locale={locale}>
      <Navbar locale={locale} />
      {children}
    </LocaleShell>
  );
}
