"use client";

import { useEffect } from "react";
import { useParams } from "next/navigation";
import { localePath } from "../../../../../lib/appPath";
import { type Locale } from "../../../../../lib/i18n";

export default function AdminLoginPage() {
  const params = useParams();
  const locale = (params?.locale as Locale) || "en";

  useEffect(() => {
    window.location.replace(localePath(locale, "admin"));
  }, [locale]);

  return (
    <main className="page narrow">
      <section className="card controls">
        <h1>Opening Admin Dashboard</h1>
        <p className="muted">Admin login is disabled. Redirecting…</p>
      </section>
    </main>
  );
}
