"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { TranslationDbSection } from "../../../../../components/TranslationDbSection";

export default function TranslationDbPage() {
  const params = useParams();
  const locale = (params?.locale as string) || "en";

  return (
    <main className="page admin-layout">
      <aside className="card admin-sidebar">
        <h3>Admin</h3>
        <p className="muted small-note">
          <Link href={`/${locale}/admin`}>← Back to dashboard</Link>
        </p>
      </aside>
      <section className="admin-content">
        <TranslationDbSection embedded />
      </section>
    </main>
  );
}
