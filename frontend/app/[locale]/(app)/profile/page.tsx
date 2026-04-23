"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { authHeaders, getToken, getStoredUser, type UserPayload } from "../../../../lib/auth";
import { localePath } from "../../../../lib/appPath";
import { type Locale, t } from "../../../../lib/i18n";

type HistoryRow = {
  crawlId: string;
  sourceUrl: string;
  siteHost: string;
  version: string;
  status: string;
  processedPages: number;
  totalFilesSaved: number;
  createdAtUtc: string;
};

export default function ProfilePage() {
  const params = useParams();
  const locale = (params?.locale as Locale) || "en";
  const [user, setUser] = useState<UserPayload | null>(null);
  const [remote, setRemote] = useState<UserPayload | null>(null);
  const [history, setHistory] = useState<HistoryRow[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setUser(getStoredUser());
  }, []);

  useEffect(() => {
    if (!getToken()) {
      return;
    }
    (async () => {
      try {
        const [p, h] = await Promise.all([
          fetch("/api/auth/profile", { headers: { ...authHeaders() } }),
          fetch("/api/auth/mirror-history?take=50", { headers: { ...authHeaders() } })
        ]);
        if (p.ok) {
          const j = (await p.json()) as {
            userId: string;
            userName: string;
            phoneNumber: string | null;
            subscriptionEndDateUtc: string | null;
          };
          setRemote({
            userId: j.userId,
            userName: j.userName,
            phoneNumber: j.phoneNumber,
            subscriptionEndDateUtc: j.subscriptionEndDateUtc
          });
        }
        if (h.ok) {
          setHistory((await h.json()) as HistoryRow[]);
        }
      } catch {
        setError("Failed to load profile.");
      }
    })();
  }, []);

  const display = remote ?? user;
  if (!getToken() || !display) {
    return (
      <main className="page narrow">
        <section className="card controls">
          <p className="muted">{t("error.signIn", locale)}</p>
          <p>
            <Link href={localePath(locale, "auth/login")}>{t("nav.signIn", locale)}</Link>
          </p>
        </section>
      </main>
    );
  }

  const end = display.subscriptionEndDateUtc ? new Date(display.subscriptionEndDateUtc) : null;
  const active = !end || end.getTime() > Date.now();

  return (
    <main className="page narrow">
      <section className="card controls">
        <h1>{t("profile.title", locale)}</h1>
        {error && <p className="error">{error}</p>}
        <dl className="profile-dl">
          <dt>Username</dt>
          <dd>{display.userName}</dd>
          <dt>Phone</dt>
          <dd>{display.phoneNumber || "—"}</dd>
          <dt>{t("profile.subscription", locale)}</dt>
          <dd>
            {end ? end.toLocaleString() : t("profile.noSubscription", locale)}{" "}
            {end ? (active ? `(${t("profile.active", locale)})` : `(${t("profile.expired", locale)})`) : null}
          </dd>
        </dl>
        <p className="muted small-note">Password is not shown for security.</p>
      </section>

      <section className="card controls">
        <h2>{t("profile.history", locale)}</h2>
        {history.length === 0 ? (
          <p className="muted">{t("profile.empty", locale)}</p>
        ) : (
          <ul className="history-list">
            {history.map((row) => (
              <li key={row.crawlId}>
                <code>{row.crawlId}</code> — {row.status} — {row.siteHost} / {row.version}
                <br />
                <span className="muted small-note">{row.sourceUrl}</span>
                <br />
                <span className="muted small-note">
                  {new Date(row.createdAtUtc).toLocaleString()} · {row.processedPages} pages · {row.totalFilesSaved} files
                </span>
              </li>
            ))}
          </ul>
        )}
      </section>
    </main>
  );
}
