"use client";

import { FormEvent, useState } from "react";
import { useParams } from "next/navigation";
import { localePath } from "../../../../../lib/appPath";
import { type Locale } from "../../../../../lib/i18n";

export default function AdminLoginPage() {
  const params = useParams();
  const locale = (params?.locale as Locale) || "en";
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    try {
      const res = await fetch("/api/admin/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ password })
      });
      if (!res.ok) {
        const text = await res.text();
        let msg = "Invalid password.";
        try {
          const data = JSON.parse(text) as { message?: string };
          msg = data.message || msg;
        } catch {
          // keep fallback
        }
        setError(msg);
        return;
      }
      window.location.replace(localePath(locale, "admin"));
    } catch {
      setError("Login failed.");
    }
  }

  return (
    <main className="page narrow">
      <section className="card controls">
        <h1>Admin Login</h1>
        <p className="muted">Enter admin password to access dashboard.</p>
        <form className="form" onSubmit={onSubmit}>
          <label htmlFor="admin-pass">Password</label>
          <input
            id="admin-pass"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
            autoComplete="current-password"
          />
          {error && <p className="error">{error}</p>}
          <button type="submit">Login</button>
        </form>
      </section>
    </main>
  );
}
