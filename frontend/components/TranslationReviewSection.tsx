"use client";

import { ChangeEvent, useMemo, useState } from "react";
import { authHeaders } from "../lib/auth";

type BlockFile = {
  page?: string;
  Page?: string;
  groups?: Array<{
    id: string;
    headingType: string;
    heading: string;
    blocks: Array<{
      id: string;
      type: string;
      original: string;
      translated: string;
    }>;
  }>;
  Groups?: Array<{
    Id?: string;
    HeadingType?: string;
    Heading?: string;
    Blocks?: Array<{
      Id?: string;
      Type?: string;
      Original?: string;
      Translated?: string;
      GroupId?: string;
    }>;
  }>;
  blocks?: Array<{
    id: string;
    type: string;
    original: string;
    translated: string;
  }>;
  Blocks?: Array<{
    Id?: string;
    Type?: string;
    Original?: string;
    Translated?: string;
    GroupId?: string;
  }>;
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

export function TranslationReviewSection() {
  const [siteHost, setSiteHost] = useState("nextjs.org");
  const [version, setVersion] = useState("16.2.4");
  const [pagePath, setPagePath] = useState("docs");
  const [rows, setRows] = useState<Row[]>([]);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [uploading, setUploading] = useState(false);
  const [uploadingCommon, setUploadingCommon] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [rowSearch, setRowSearch] = useState("");
  const [statusFilter, setStatusFilter] = useState<StatusFilter>("all");
  const [previewLanguage, setPreviewLanguage] = useState("fa");

  const changedCount = useMemo(() => rows.filter((r) => r.fa !== r.en && r.fa.trim().length > 0).length, [rows]);

  const filteredRows = useMemo(() => {
    return rows.filter((r) => rowMatchesText(r, rowSearch) && rowMatchesStatus(r, statusFilter));
  }, [rows, rowSearch, statusFilter]);

  async function loadPage() {
    setLoading(true);
    setError(null);
    setMessage(null);
    try {
      const normalizedPage = normalizePagePath(pagePath);
      const base = `/mirror/${siteHost.trim()}/${version.trim()}/_i18n`;
      const pageCandidates = [`${base}/blocks/${normalizedPage}.json`];

      let pageCatalog: BlockFile | null = null;
      for (const candidate of pageCandidates) {
        const res = await fetch(candidate, { cache: "no-store" });
        if (res.ok) {
          pageCatalog = (await res.json()) as BlockFile;
          break;
        }
      }
      const nestedBlocksLower = (pageCatalog?.groups ?? []).flatMap((group) => group.blocks ?? []);
      const nestedBlocksUpper = (pageCatalog?.Groups ?? []).flatMap((group) => group.Blocks ?? []);
      const topLevelLower = pageCatalog?.blocks ?? [];
      const topLevelUpper = pageCatalog?.Blocks ?? [];
      const sourceBlocks =
        nestedBlocksLower.length > 0
          ? nestedBlocksLower
          : nestedBlocksUpper.length > 0
            ? nestedBlocksUpper
            : topLevelLower.length > 0
              ? topLevelLower
              : topLevelUpper;
      if (sourceBlocks.length === 0) {
        throw new Error("Block page not found. Check site/version/page path.");
      }

      const loadedRows = sourceBlocks.map((block) => {
        const id = "id" in block ? block.id : (block as { Id?: string }).Id ?? "";
        const original =
          "original" in block ? block.original : (block as { Original?: string }).Original ?? "";
        const translated =
          "translated" in block ? block.translated : (block as { Translated?: string }).Translated ?? "";

        return {
          key: id,
          en: original,
          fa: translated.trim().length ? translated : original
        };
      });
      setRows(loadedRows);
      setMessage(`Loaded ${loadedRows.length} blocks from ${normalizedPage}.json`);
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

  function updateEn(key: string, value: string) {
    setRows((prev) => prev.map((row) => (row.key === key ? { ...row, en: value } : row)));
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
      const sourceEntries: Record<string, string> = {};
      for (const row of rows) {
        sourceEntries[row.key] = row.en;
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
          pagePath: normalizePagePath(pagePath),
          entries,
          sourceEntries
        })
      });

      const text = await response.text();
      const payload = text ? (JSON.parse(text) as { message?: string; rebuiltPageCount?: number }) : {};
      if (!response.ok) {
        throw new Error(payload.message || `Update failed (${response.status}).`);
      }
      setMessage(
        `Saved ${Object.keys(entries).length} block translations and rebuilt ${payload.rebuiltPageCount ?? 0} page(s).`
      );
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
      form.set("pagePath", normalizePagePath(pagePath));
      form.set("file", file, file.name);

      const response = await fetch("/api/mirror/update-translations/upload", {
        method: "POST",
        headers: {
          ...authHeaders()
        },
        body: form
      });

      const text = await response.text();
      const payload = text
        ? (JSON.parse(text) as { message?: string; updatedEntryCount?: number; rebuiltPageCount?: number })
        : {};
      if (!response.ok) {
        throw new Error(payload.message || `Upload failed (${response.status}).`);
      }

      setMessage(
        `Uploaded ${payload.updatedEntryCount ?? 0} block translations and rebuilt ${payload.rebuiltPageCount ?? 0} page(s).`
      );
      await loadPage();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Failed to upload JSON.");
    } finally {
      setUploading(false);
      event.target.value = "";
    }
  }

  async function onUploadCommonJson(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    if (!file) {
      return;
    }

    setUploadingCommon(true);
    setError(null);
    setMessage(null);
    try {
      const form = new FormData();
      form.set("siteHost", siteHost.trim());
      form.set("version", version.trim());
      form.set("language", "fa");
      form.set("file", file, file.name);

      const response = await fetch("/api/mirror/update-common-translations/upload", {
        method: "POST",
        headers: {
          ...authHeaders()
        },
        body: form
      });

      const text = await response.text();
      const payload = text
        ? (JSON.parse(text) as { message?: string; rebuiltPageCount?: number; updatedCommonCount?: number })
        : {};
      if (!response.ok) {
        throw new Error(payload.message || `Common upload failed (${response.status}).`);
      }

      setMessage(
        `Applied common JSON (${payload.updatedCommonCount ?? 0} entries) and rebuilt ${payload.rebuiltPageCount ?? 0} page(s).`
      );
      await loadPage();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Failed to upload common JSON.");
    } finally {
      setUploadingCommon(false);
      event.target.value = "";
    }
  }

  return (
    <section className="card controls">
      <h2>Translation Review (EN vs FA)</h2>
      <p className="muted">Load one page from block JSON, compare EN/FA, click FA cell to edit, then submit all changes.</p>

      <div className="translation-common-panel">
        <div>
          <h3>Apply `_common.json` To All Pages</h3>
          <p className="muted">
            Upload a common translation JSON file and apply it site-wide for this host/version.
          </p>
        </div>
        <label className="btn-ghost file-upload">
          {uploadingCommon ? "Applying..." : "Upload Common JSON + Rebuild All"}
          <input
            type="file"
            accept=".json,application/json"
            onChange={onUploadCommonJson}
            hidden
            disabled={uploadingCommon}
          />
        </label>
      </div>

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
          {loading ? "Loading..." : "Load Page Blocks"}
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
        <div className="card viewer" style={{ marginTop: 12 }}>
          <div className="viewer-header">
            <h2>Mirrored Preview</h2>
            <input
              value={previewLanguage}
              onChange={(e) => setPreviewLanguage(e.target.value.toLowerCase())}
              placeholder="fa"
              style={{ maxWidth: 120 }}
            />
          </div>
          <iframe
            key={`${siteHost}-${version}-${pagePath}-${previewLanguage}`}
            src={`/mirror/${siteHost.trim()}/${version.trim()}/_localized/${previewLanguage}/${normalizePagePath(pagePath)}.html`}
            title="Translation preview"
          />
        </div>
      )}

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
          <div className="translation-head">English (editable)</div>
          <div className="translation-head">Persian (editable)</div>
          {filteredRows.length === 0 ? (
            <div className="translation-empty">No rows match the current filter. Clear the search or change &quot;Show&quot;.</div>
          ) : (
            filteredRows.map((row) => {
              return (
                <div className="translation-row" key={row.key}>
                  <code className="translation-key">{row.key}</code>
                  <div className="translation-en">
                    <textarea
                      value={row.en}
                      onChange={(e) => updateEn(row.key, e.target.value)}
                      rows={2}
                    />
                  </div>
                  <div className="translation-fa">
                    <textarea
                      value={row.fa}
                      onChange={(e) => updateFa(row.key, e.target.value)}
                      rows={2}
                    />
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
