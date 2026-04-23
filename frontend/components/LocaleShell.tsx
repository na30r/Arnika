"use client";

import { useEffect } from "react";
import { isRtl, type Locale } from "../lib/i18n";

export function LocaleShell({ locale, children }: { locale: Locale; children: React.ReactNode }) {
  useEffect(() => {
    document.documentElement.lang = locale;
    document.documentElement.dir = isRtl(locale) ? "rtl" : "ltr";
  }, [locale]);
  return <>{children}</>;
}
