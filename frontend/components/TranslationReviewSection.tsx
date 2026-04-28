"use client";

import { ChangeEvent, useEffect, useMemo, useState } from "react";
import { authHeaders } from "../lib/auth";

type CatalogFile = {
  Language?: string;
  Entries?: Record<string, string>;
};

type Row = {
  key: string;
  en: string;
  fa: string;
};

type StatusFilter = "all" | "untranslated" | "translated";

function rowMatchesText(row: Row, query: string): boolean {
  const q = query.trim();
  if (!q) {
    return true;
  }
  const lower = q.toLowerCase();
  return (
    row.key.toLowerCase().includes(lower) ||
    row.en.toLowerCase().includes(lower) ||
    row.fa.toLowerCase().includes(lower)
  );
}

function rowMatchesStatus(row: Row, status: StatusFilter): boolean {
  if (status === "all") {
    return true;
  }
  const untranslated = row.fa === row.en;
  return status === "untranslated" ? untranslated : !untranslated;
}

function normalizePagePath(input: string): string {
  const cleaned = input.trim().replace(/\\/g, "/").replace(/^\/+/, "");
  if (!cleaned) {
    return "docs";
  }
  return cleaned.replace(/\.json$/i, "").replace(/\.html$/i, "");
}

function toTargetHtml(pagePath: string): string {
  return `${normalizePagePath(pagePath)}.html`;
}

export function TranslationReviewSection() {
  const [siteHost, setSiteHost] = useState("nextjs.org");
  const [version, setVersion] = useState("16.2.4");
  const [pagePath, setPagePath] = useState("docs");
  const [rows, setRows] = useState<Row[]>([]);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [uploading, setUploading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [editingKey, setEditingKey] = useState<string | null>(null);
  const [rowSearch, setRowSearch] = useState("");
  const [statusFilter, setStatusFilter] = useState<StatusFilter>("all");

  const changedCount = useMemo(() => rows.filter((r) => r.fa !== r.en && r.fa.trim().length > 0).length, [rows]);

  const filteredRows = useMemo(() => {
    return rows.filter((r) => rowMatchesText(r, rowSearch) && rowMatchesStatus(r, statusFilter));
  }, [rows, rowSearch, statusFilter]);

  useEffect(() => {
    if (editingKey && !filteredRows.some((r) => r.key === editingKey)) {
      setEditingKey(null);
    }
  }, [editingKey, filteredRows]);

  async function loadPage() {
    setLoading(true);
    setError(null);
    setMessage(null);
    try {
      const normalizedPage = normalizePagePath(pagePath);
      const base = `/mirror/${siteHost.trim()}/${version.trim()}/_i18n`;
      const pageCandidates = [
        `${base}/pages/${normalizedPage}.json`,
        `${base}/pages/en/${normalizedPage}.json`
      ];

      let pageCatalog: CatalogFile | null = null;
      for (const candidate of pageCandidates) {
        const res = await fetch(candidate, { cache: "no-store" });
        if (res.ok) {
          pageCatalog = (await res.json()) as CatalogFile;
          break;
        }
      }
      if (!pageCatalog?.Entries) {
        throw new Error("Page catalog not found. Check site/version/page path.");
      }

      const faRes = await fetch(`${base}/fa.json`, { cache: "no-store" });
      if (!faRes.ok) {
        throw new Error("fa.json not found.");
      }
      const faCatalog = (await faRes.json()) as CatalogFile;
      const faEntries = faCatalog.Entries ?? {};

      const loadedRows = Object.entries(pageCatalog.Entries).map(([key, en]) => ({
        key,
        en,
        fa: faEntries[key] ?? en
      }));
      setRows(loadedRows);
      setMessage(`Loaded ${loadedRows.length} entries from ${normalizedPage}.json`);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Failed to load translation page.");
      setRows([]);
    } finally {
      setLoading(false);
    }
  }

  function updateFa(key: string, value: string) {
    setRows((prev) => prev.map((row) => (row.key === key ? { ...row, fa: value } : row)));
  }

  async function submitChanges() {
    if (rows.length === 0) {
      setError("Load a page first.");
      return;
    }
    setSaving(true);
    setError(null);
    setMessage(null);
    try {
      const entries: Record<string, string> = {};
      for (const row of rows) {
        if (row.fa !== row.en) {
          entries[row.key] = row.fa;
        }
      }

      const response = await fetch("/api/mirror/update-translations", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          ...authHeaders()
        },
        body: JSON.stringify({
          siteHost: siteHost.trim(),
          version: version.trim(),
          language: "fa",
          entries,
          targetPages: [toTargetHtml(pagePath)]
        })
      });

      const text = await response.text();
      const payload = text ? (JSON.parse(text) as { message?: string; rebuiltPageCount?: number }) : {};
      if (!response.ok) {
        throw new Error(payload.message || `Update failed (${response.status}).`);
      }
      setMessage(`Saved ${Object.keys(entries).length} changes and rebuilt ${payload.rebuiltPageCount ?? 0} page(s).`);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Failed to submit changes.");
    } finally {
      setSaving(false);
    }
  }

  async function onUploadJson(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    if (!file) {
      return;
    }
    setUploading(true);
    setError(null);
    setMessage(null);
    try {
      const form = new FormData();
      form.set("siteHost", siteHost.trim());
      form.set("version", version.trim());
      form.set("language", "fa");
      form.append("targetPages", toTargetHtml(pagePath));
      form.set("file", file, file.name);

      const response = await fetch("/api/mirror/update-translations/upload", {
        method: "POST",
        headers: {
          ...authHeaders()
        },
        body: form
      });

      const text = await response.text();
      const payload = text ? (JSON.parse(text) as { message?: string; rebuiltPageCount?: number; updatedEntryCount?: number }) : {};
      if (!response.ok) {
        throw new Error(payload.message || `Upload failed (${response.status}).`);
      }

      setMessage(
        `Uploaded ${payload.updatedEntryCount ?? 0} entries and rebuilt ${payload.rebuiltPageCount ?? 0} page(s).`
      );
      await loadPage();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Failed to upload JSON.");
    } finally {
      setUploading(false);
      event.target.value = "";
    }
  }

  return (
    <section className="card controls">
      <h2>Translation Review (EN vs FA)</h2>
      <p className="muted">Load one page, compare texts, click FA cell to edit, then submit all changes.</p>

      <div className="form translation-grid">
        <label htmlFor="tr-site">Site Host</label>
        <input id="tr-site" value={siteHost} onChange={(e) => setSiteHost(e.target.value)} />
        <label htmlFor="tr-version">Version</label>
        <input id="tr-version" value={version} onChange={(e) => setVersion(e.target.value)} />
        <label htmlFor="tr-page">Page Path</label>
        <input id="tr-page" value={pagePath} onChange={(e) => setPagePath(e.target.value)} placeholder="docs or blog/1" />
      </div>

      <div className="row">
        <button type="button" onClick={loadPage} disabled={loading}>
          {loading ? "Loading..." : "Load Page Entries"}
        </button>
        <button type="button" onClick={submitChanges} disabled={saving || rows.length === 0}>
          {saving ? "Saving..." : `Submit All Changes (${changedCount})`}
        </button>
        <label className="btn-ghost file-upload">
          {uploading ? "Uploading..." : "Upload JSON + Rebuild"}
          <input type="file" accept=".json,application/json" onChange={onUploadJson} hidden disabled={uploading} />
        </label>
      </div>

      {error && <p className="error">{error}</p>}
      {message && <p className="muted">{message}</p>}

      {rows.length > 0 && (
        <div className="translation-filters" role="search">
          <label htmlFor="tr-row-search" className="sr-only">
            Search keys and text
          </label>
          <input
            id="tr-row-search"
            type="search"
            className="translation-search-input"
            value={rowSearch}
            onChange={(e) => setRowSearch(e.target.value)}
            placeholder="Filter by key, English, or Persian…"
            autoComplete="off"
          />
          <label htmlFor="tr-status-filter" className="translation-filter-label">
            Show
          </label>
          <select
            id="tr-status-filter"
            className="translation-status-select"
            value={statusFilter}
            onChange={(e) => setStatusFilter(e.target.value as StatusFilter)}
            aria-label="Filter by translation status"
          >
            <option value="all">All rows</option>
            <option value="untranslated">Untranslated (FA same as EN)</option>
            <option value="translated">Translated (FA differs from EN)</option>
          </select>
          <span className="translation-filter-count" aria-live="polite">
            {filteredRows.length === rows.length
              ? `${rows.length} entries`
              : `Showing ${filteredRows.length} of ${rows.length}`}
          </span>
        </div>
      )}

      {rows.length > 0 && (
        <div className="translation-table">
          <div className="translation-head">Key</div>
          <div className="translation-head">English</div>
          <div className="translation-head">Persian (click to edit)</div>
          {filteredRows.length === 0 ? (
            <div className="translation-empty">No rows match the current filter. Clear the search or change &quot;Show&quot;.</div>
          ) : (
            filteredRows.map((row) => {
              const isEditing = editingKey === row.key;
              return (
                <div className="translation-row" key={row.key}>
                  <code className="translation-key">{row.key}</code>
                  <div className="translation-en">{row.en}</div>
                  <div className="translation-fa">
                    {isEditing ? (
                      <textarea
                        value={row.fa}
                        onChange={(e) => updateFa(row.key, e.target.value)}
                        onBlur={() => setEditingKey(null)}
                        rows={2}
                        autoFocus
                      />
                    ) : (
                      <button type="button" className="translation-editable" onClick={() => setEditingKey(row.key)}>
                        {row.fa}
                      </button>
                    )}
                  </div>
                </div>
              );
            })
          )}
        </div>
      )}
    </section>
  );
}
