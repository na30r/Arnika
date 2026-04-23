"use client";

import { FormEvent, useState } from "react";
import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { saveSession, type UserPayload } from "../../../lib/auth";
import { type Locale, t } from "../../../lib/i18n";

export default function RegisterPage() {
  const params = useParams();
  const router = useRouter();
  const locale = (params?.locale as Locale) || "en";
  const base = `/${locale}`;
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
      const data = (await res.json()) as {
        token?: string;
        userId?: string;
        userName?: string;
        phoneNumber?: string | null;
        subscriptionEndDateUtc?: string | null;
        message?: string;
      };
      if (!res.ok) {
        throw new Error(data.message ?? "Registration failed.");
      }
      if (!data.token || !data.userId || !data.userName) {
        throw new Error("Invalid response from server.");
      }
      const user: UserPayload = {
        userId: data.userId,
        userName: data.userName,
        phoneNumber: data.phoneNumber ?? null,
        subscriptionEndDateUtc: data.subscriptionEndDateUtc ?? null
      };
      saveSession(data.token, user);
      router.push(base);
      router.refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Error");
    } finally {
      setLoading(false);
    }
  }

  return (
    <main className="page narrow">
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
          <Link href={`${base}/login`}>{t("auth.haveAccount", locale)}</Link>
        </p>
      </section>
    </main>
  );
}
