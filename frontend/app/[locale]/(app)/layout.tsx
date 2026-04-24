import { locales, type Locale } from "../../../lib/i18n";
import { notFound } from "next/navigation";

type Props = {
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
};

/** App routes; navbar is provided by the parent `[locale]/layout`. */
export default async function AppLayout({ children, params }: Props) {
  const { locale: raw } = await params;
  if (!locales.includes(raw as Locale)) {
    notFound();
  }
  return <>{children}</>;
}
