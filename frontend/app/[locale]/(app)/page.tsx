"use client";

import { useEffect } from "react";
import { useParams } from "next/navigation";
import { localePath } from "../../../lib/appPath";
import { type Locale } from "../../../lib/i18n";

export default function HomePage() {
  const params = useParams();
  const locale = (params?.locale as Locale) || "en";

  useEffect(() => {
    window.location.replace(localePath(locale, "admin"));
  }, [locale]);

  return null;
}
