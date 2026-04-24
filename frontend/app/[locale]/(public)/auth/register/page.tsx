"use client";

import { FormEvent, useState } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { saveSession, type UserPayload } from "../../../../../lib/auth";
import { localePath } from "../../../../../lib/appPath";
import { type Locale, t } from "../../../../../lib/i18n";

export default function RegisterPage() {
  const params = useParams();
  const locale = (params?.locale as Locale) || "en";
  const [userName, setUserName] = useState("");
  const [phoneNumber, setPhoneNumber] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setLoading(true);
    setError(null);
    try {
      const res = await fetch("/api/auth/register", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          userName,
          phoneNumber: phoneNumber.trim() || null,
          password
        })
      });
      const text = await res.text();
      let data: {
        token?: string;
        userId?: string;
        userName?: string;
        phoneNumber?: string | null;
        subscriptionEndDateUtc?: string | null;
        message?: string;
      } = {};
      try {
        data = text ? (JSON.parse(text) as typeof data) : {};
      } catch {
        setError(res.ok ? "Invalid response from server." : text.slice(0, 200) || `Error ${res.status}`);
        return;
      }
      if (!res.ok) {
        setError(data.message ?? `Registration failed (${res.status}).`);
        return;
      }
      if (!data.token || !data.userId || !data.userName) {
        setError("Invalid response: missing token or user.");
        return;
      }
      const user: UserPayload = {
        userId: data.userId,
        userName: data.userName,
        phoneNumber: data.phoneNumber ?? null,
        subscriptionEndDateUtc: data.subscriptionEndDateUtc ?? null
      };
      await saveSession(data.token, user);
      window.location.assign(localePath(locale, ""));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Network error.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <main className="page narrow">
      <p className="muted small-note" style={{ marginBottom: "0.5rem" }}>
        <a href={localePath("en", "auth/register")}>EN</a>
        {" · "}
        <a href={localePath("fa", "auth/register")}>FA</a>
      </p>
      <section className="card controls">
        <h1>{t("auth.registerTitle", locale)}</h1>
        <form className="form" onSubmit={onSubmit}>
          <label htmlFor="u">{t("auth.username", locale)}</label>
          <input id="u" value={userName} onChange={(e) => setUserName(e.target.value)} required minLength={2} autoComplete="username" />
          <label htmlFor="ph">{t("auth.phone", locale)}</label>
          <input id="ph" value={phoneNumber} onChange={(e) => setPhoneNumber(e.target.value)} autoComplete="tel" />
          <label htmlFor="p">{t("auth.password", locale)}</label>
          <input
            id="p"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
            minLength={6}
            autoComplete="new-password"
          />
          {error && <p className="error">{error}</p>}
          <button type="submit" disabled={loading}>
            {loading ? t("auth.signingIn", locale) : t("auth.submitRegister", locale)}
          </button>
        </form>
        <p className="muted">
          <Link href={localePath(locale, "auth/login")}>{t("auth.haveAccount", locale)}</Link>
        </p>
      </section>
    </main>
  );
}
