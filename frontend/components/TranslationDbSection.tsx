"use client";

import { FormEvent, useEffect, useMemo, useState } from "react";
import { authHeaders } from "../lib/auth";

type ArchiveRow = {
  id?: number;
  scope?: string;
  siteHost?: string;
  version?: string;
  language?: string;
  pagePath?: string | null;
  translationKey?: string;
  originalText?: string | null;
  translatedValue?: string;
  savedAtUtc?: string;
};

type ApplyResult = {
  siteHost?: string;
  version?: string;
  language?: string;
  rebuiltPages?: string[];
  rebuiltPageCount?: number;
  updatedEntryCount?: number;
  message?: string;
};

type Props = {
  /** When true, omit top-level page chrome (use inside admin dashboard). */
  embedded?: boolean;
};

export function TranslationDbSection({ embedded = false }: Props) {
  const [siteHost, setSiteHost] = useState("nextjs.org");
  const [version, setVersion] = useState("16.2.4");
  const [language, setLanguage] = useState("fa");
  const [scope, setScope] = useState("");
  const [take, setTake] = useState(1000);
  const [rows, setRows] = useState<ArchiveRow[]>([]);
  const [loading, setLoading] = useState(false);
  const [applyLoading, setApplyLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [applyResult, setApplyResult] = useState<ApplyResult | null>(null);
  const [siteOrigin, setSiteOrigin] = useState("");

  useEffect(() => {
    setSiteOrigin(typeof window !== "undefined" ? window.location.origin : "");
  }, []);

  const previewUrl = useMemo(() => {
    if (!siteOrigin || !applyResult?.rebuiltPages?.length) return "";
    const h = applyResult.siteHost ?? siteHost;
    const v = applyResult.version ?? version;
    const lang = applyResult.language ?? language;
    const rel = (applyResult.rebuiltPages[0] ?? "").replace(/^\/+/, "");
    return `${siteOrigin}/mirror/${h}/${v}/_localized/${lang}/${rel}`;
  }, [applyResult, siteHost, version, language, siteOrigin]);

  function buildQueryString(): string {
    const p = new URLSearchParams();
    if (siteHost.trim()) p.set("siteHost", siteHost.trim());
    if (version.trim()) p.set("version", version.trim());
    if (language.trim()) p.set("language", language.trim());
    if (scope.trim()) p.set("scope", scope.trim());
    p.set("take", String(Math.min(5000, Math.max(1, take))));
    return p.toString();
  }

  async function loadFromDb(e?: FormEvent) {
    e?.preventDefault();
    setLoading(true);
    setError(null);
    setApplyResult(null);
    try {
      const res = await fetch(`/api/translation-archive?${buildQueryString()}`, {
        headers: { ...authHeaders() },
        cache: "no-store"
      });
      const text = await res.text();
      const data = text ? (JSON.parse(text) as ArchiveRow[] | { message?: string }) : [];
      if (!res.ok) {
        const msg = Array.isArray(data) ? `Failed (${res.status})` : data.message;
        throw new Error(msg || `Request failed (${res.status}).`);
      }
      setRows(Array.isArray(data) ? data : []);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Load failed.");
      setRows([]);
    } finally {
      setLoading(false);
    }
  }

  async function downloadJson() {
    setError(null);
    try {
      const res = await fetch(`/api/translation-archive/download?${buildQueryString()}`, {
        headers: { ...authHeaders() },
        cache: "no-store"
      });
      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        throw new Error((err as { message?: string }).message || `Download failed (${res.status}).`);
      }
      const blob = await res.blob();
      const cd = res.headers.get("content-disposition");
      let name = "translation-archive.json";
      const m = cd?.match(/filename\*?=(?:UTF-8'')?["']?([^"';]+)/i);
      if (m?.[1]) name = decodeURIComponent(m[1].trim());
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = name;
      a.click();
      URL.revokeObjectURL(url);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Download failed.");
    }
  }

  async function applyCatalogRebuild() {
    setApplyLoading(true);
    setError(null);
    setApplyResult(null);
    try {
      const res = await fetch("/api/translation-archive/apply-catalog", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          ...authHeaders()
        },
        body: JSON.stringify({
          siteHost: siteHost.trim(),
          version: version.trim() || "latest",
          language: language.trim() || "en"
        }),
        cache: "no-store"
      });
      const data = (await res.json().catch(() => ({}))) as ApplyResult & { message?: string };
      if (!res.ok) {
        throw new Error(data.message || `Apply failed (${res.status}).`);
      }
      setApplyResult(data);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Apply failed.");
    } finally {
      setApplyLoading(false);
    }
  }

  return (
    <div className={embedded ? "" : "page"}>
      {!embedded ? (
        <header className="card controls" style={{ marginBottom: "1rem" }}>
          <h1>Translation database</h1>
          <p className="muted">
            Browse archived translation rows, download JSON, or rebuild localized HTML from the latest{" "}
            <strong>catalog</strong> snapshot in SQL (same merge as Translation Review).
          </p>
        </header>
      ) : null}

      <section className="card controls">
        {!embedded ? null : (
          <>
            <h2>Translation database</h2>
            <p className="muted">
              Query <code>TranslationArchive</code>, download JSON, or apply latest <strong>catalog</strong> rows to the
              mirror and preview rebuilt pages.
            </p>
          </>
        )}
        <form className="form" onSubmit={loadFromDb}>
          <div className="row log-toolbar" style={{ flexWrap: "wrap", gap: "0.75rem" }}>
            <label className="log-field">
              Site host
              <input value={siteHost} onChange={(e) => setSiteHost(e.target.value)} placeholder="nextjs.org" />
            </label>
            <label className="log-field">
              Version
              <input value={version} onChange={(e) => setVersion(e.target.value)} placeholder="16.2.4" />
            </label>
            <label className="log-field">
              Language
              <input value={language} onChange={(e) => setLanguage(e.target.value)} placeholder="fa" />
            </label>
            <label className="log-field">
              Scope
              <select value={scope} onChange={(e) => setScope(e.target.value)}>
                <option value="">All</option>
                <option value="catalog">catalog</option>
                <option value="common">common</option>
                <option value="block">block</option>
              </select>
            </label>
            <label className="log-field">
              Max rows
              <input
                type="number"
                min={1}
                max={5000}
                value={take}
                onChange={(e) => setTake(Number(e.target.value) || 1000)}
              />
            </label>
          </div>
          <div className="row" style={{ gap: "0.5rem", flexWrap: "wrap" }}>
            <button type="submit" disabled={loading}>
              {loading ? "Loading…" : "Load from database"}
            </button>
            <button type="button" onClick={() => void downloadJson()} disabled={loading}>
              Download JSON file
            </button>
            <button type="button" onClick={() => void applyCatalogRebuild()} disabled={applyLoading}>
              {applyLoading ? "Applying…" : "Apply catalog from DB & rebuild pages"}
            </button>
          </div>
        </form>
        {error ? <p className="error">{error}</p> : null}
        {applyResult?.rebuiltPageCount != null ? (
          <p className="muted">
            Rebuilt <strong>{applyResult.rebuiltPageCount}</strong> page(s); merged <strong>{applyResult.updatedEntryCount ?? 0}</strong> catalog
            keys from archive.
          </p>
        ) : null}
      </section>

      {rows.length > 0 ? (
        <section className="card controls" style={{ marginTop: "1rem" }}>
          <h3>Rows ({rows.length})</h3>
          <div className="log-table-wrap translation-db-table">
            <table className="log-table">
              <thead>
                <tr>
                  <th>Saved (UTC)</th>
                  <th>Scope</th>
                  <th>Key</th>
                  <th>Translated</th>
                  <th>Page</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.id}>
                    <td className="log-cell-time">
                      <code>{(r.savedAtUtc ?? "").replace("T", " ").replace("Z", "")}</code>
                    </td>
                    <td>{r.scope ?? ""}</td>
                    <td className="log-cell-msg">
                      <pre>{r.translationKey ?? ""}</pre>
                    </td>
                    <td className="log-cell-msg">
                      <pre>{r.translatedValue ?? ""}</pre>
                    </td>
                    <td>
                      <code>{r.pagePath ?? "—"}</code>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ) : !loading ? (
        <p className="muted" style={{ marginTop: "1rem" }}>
          No rows loaded. Adjust filters and click &quot;Load from database&quot;.
        </p>
      ) : null}

      {previewUrl ? (
        <section className="card viewer" style={{ marginTop: "1rem" }}>
          <div className="viewer-header">
            <h2>Preview (first rebuilt page)</h2>
            <a href={previewUrl} target="_blank" rel="noreferrer">
              Open in new tab
            </a>
          </div>
          <iframe title="Rebuilt localized preview" src={previewUrl} className="translation-db-preview-frame" />
        </section>
      ) : null}
    </div>
  );
}
