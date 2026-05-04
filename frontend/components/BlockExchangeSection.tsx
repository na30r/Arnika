"use client";

import { FormEvent, useCallback, useEffect, useMemo, useState } from "react";
import { authHeaders } from "../lib/auth";

type MergeFlatResponse = {
  blockPageJson?: string;
  items?: { id: string; type: string; original: string; translated: string; groupId?: string | null }[];
  unmatchedTranslationKeys?: string[];
};

type ToFlatResponse = {
  entries?: Record<string, string>;
  entriesJson?: string;
};

type PerPageFlatExportRow = {
  pagePath: string;
  ok: boolean;
  fileName?: string;
  entryCount?: number;
  error?: string;
};

function flatDownloadSlug(pagePath: string): string {
  return pagePath
    .replace(/\//g, "_")
    .replace(/[^a-zA-Z0-9._-]+/g, "_")
    .replace(/_+/g, "_")
    .replace(/^_|_$/g, "") || "page";
}

function downloadText(filename: string, text: string, mime: string) {
  const blob = new Blob([text], { type: mime });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

async function fetchJson<T>(url: string): Promise<T> {
  const res = await fetch(url, { headers: { ...authHeaders() }, cache: "no-store" });
  const data = (await res.json().catch(() => null)) as T & { message?: string };
  if (!res.ok) {
    throw new Error((data as { message?: string })?.message || `Request failed (${res.status}).`);
  }
  return data as T;
}

export function BlockExchangeSection() {
  const [siteHost, setSiteHost] = useState("");
  const [version, setVersion] = useState("");
  const [useMirrorPath, setUseMirrorPath] = useState(true);
  const [blockPageText, setBlockPageText] = useState("");
  const [translationsText, setTranslationsText] = useState('{\n  "What is Next.js?": "نکست جی‌اس چیست؟"\n}');
  const [emptyUsesOriginal, setEmptyUsesOriginal] = useState(true);
  const [mergeResult, setMergeResult] = useState<MergeFlatResponse | null>(null);
  const [mergeError, setMergeError] = useState<string | null>(null);
  const [mergeBusy, setMergeBusy] = useState(false);

  const [flatResult, setFlatResult] = useState<ToFlatResponse | null>(null);
  const [flatError, setFlatError] = useState<string | null>(null);
  const [flatBusy, setFlatBusy] = useState(false);
  const [flatFillEmptyWithOriginal, setFlatFillEmptyWithOriginal] = useState(true);

  const [hosts, setHosts] = useState<string[]>([]);
  const [versions, setVersions] = useState<string[]>([]);
  const [pages, setPages] = useState<string[]>([]);
  const [selectedPages, setSelectedPages] = useState<Record<string, boolean>>({});
  const [catalogBusy, setCatalogBusy] = useState(false);
  const [catalogError, setCatalogError] = useState<string | null>(null);

  const [perPageFlatResults, setPerPageFlatResults] = useState<PerPageFlatExportRow[] | null>(null);
  const [batchError, setBatchError] = useState<string | null>(null);
  const [batchBusy, setBatchBusy] = useState(false);

  const selectedCount = useMemo(
    () => pages.reduce((n, p) => n + (selectedPages[p] ? 1 : 0), 0),
    [pages, selectedPages]
  );

  const loadHosts = useCallback(async () => {
    setCatalogBusy(true);
    setCatalogError(null);
    try {
      const list = await fetchJson<string[]>("/api/mirror/block-catalog/hosts");
      setHosts(list);
      setSiteHost((prev) => {
        if (prev && list.includes(prev)) return prev;
        return list[0] ?? "";
      });
    } catch (e) {
      setHosts([]);
      setCatalogError(e instanceof Error ? e.message : "Failed to load hosts.");
    } finally {
      setCatalogBusy(false);
    }
  }, []);

  useEffect(() => {
    if (!useMirrorPath) {
      return;
    }
    void loadHosts();
  }, [useMirrorPath, loadHosts]);

  useEffect(() => {
    if (!useMirrorPath || !siteHost.trim()) {
      setVersions([]);
      setVersion("");
      setPages([]);
      setSelectedPages({});
      return;
    }

    let cancelled = false;
    (async () => {
      setCatalogBusy(true);
      setCatalogError(null);
      try {
        const list = await fetchJson<string[]>(
          `/api/mirror/block-catalog/versions?siteHost=${encodeURIComponent(siteHost.trim())}`
        );
        if (cancelled) return;
        setVersions(list);
        setVersion((prev) => {
          if (prev && list.includes(prev)) return prev;
          return list[0] ?? "";
        });
      } catch (e) {
        if (!cancelled) {
          setVersions([]);
          setVersion("");
          setCatalogError(e instanceof Error ? e.message : "Failed to load versions.");
        }
      } finally {
        if (!cancelled) setCatalogBusy(false);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [useMirrorPath, siteHost]);

  useEffect(() => {
    if (!useMirrorPath || !siteHost.trim() || !version.trim()) {
      setPages([]);
      setSelectedPages({});
      return;
    }

    let cancelled = false;
    (async () => {
      setCatalogBusy(true);
      setCatalogError(null);
      try {
        const list = await fetchJson<string[]>(
          `/api/mirror/block-catalog/pages?siteHost=${encodeURIComponent(siteHost.trim())}&version=${encodeURIComponent(version.trim())}`
        );
        if (cancelled) return;
        setPages(list);
        setSelectedPages({});
      } catch (e) {
        if (!cancelled) {
          setPages([]);
          setSelectedPages({});
          setCatalogError(e instanceof Error ? e.message : "Failed to load pages.");
        }
      } finally {
        if (!cancelled) setCatalogBusy(false);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [useMirrorPath, siteHost, version]);

  function togglePage(path: string) {
    setSelectedPages((prev) => ({ ...prev, [path]: !prev[path] }));
  }

  function selectAllPages() {
    const next: Record<string, boolean> = {};
    for (const p of pages) next[p] = true;
    setSelectedPages(next);
  }

  function clearPageSelection() {
    setSelectedPages({});
  }

  function getExactlyOneSelectedPage(): string {
    const checked = pages.filter((p) => selectedPages[p]);
    if (checked.length !== 1) {
      throw new Error(
        "For merge or single-page flat export, select exactly one page in the list below (use checkboxes)."
      );
    }
    return checked[0]!;
  }

  function getMultiSelectedPages(): string[] {
    return pages.filter((p) => selectedPages[p]);
  }

  async function onMergeSubmit(e: FormEvent) {
    e.preventDefault();
    setMergeBusy(true);
    setMergeError(null);
    setMergeResult(null);
    try {
      let translations: Record<string, string>;
      try {
        translations = JSON.parse(translationsText || "{}") as Record<string, string>;
      } catch {
        throw new Error("Translations field must be valid JSON object (source text or b_… id → string).");
      }
      if (!translations || typeof translations !== "object" || Array.isArray(translations)) {
        throw new Error("Translations must be a JSON object.");
      }

      const body: Record<string, unknown> = {
        translations,
        emptyTranslationUsesOriginal: emptyUsesOriginal
      };
      if (useMirrorPath) {
        if (!siteHost.trim() || !version.trim()) {
          throw new Error("Choose a site host and version from the lists.");
        }
        body.siteHost = siteHost.trim();
        body.version = version.trim();
        body.pagePath = getExactlyOneSelectedPage();
      } else {
        if (!blockPageText.trim()) {
          throw new Error("Paste block page JSON or enable “Mirror on disk”.");
        }
        body.blockPageJson = blockPageText;
      }

      const res = await fetch("/api/mirror/block-exchange/merge-flat", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          ...authHeaders()
        },
        body: JSON.stringify(body)
      });
      const data = (await res.json().catch(() => null)) as MergeFlatResponse & { message?: string };
      if (!res.ok) {
        throw new Error(data?.message || `Merge failed (${res.status}).`);
      }
      setMergeResult(data);
    } catch (err) {
      setMergeError(err instanceof Error ? err.message : "Merge failed.");
    } finally {
      setMergeBusy(false);
    }
  }

  async function onFlatSubmit(e: FormEvent) {
    e.preventDefault();
    setFlatBusy(true);
    setFlatError(null);
    setFlatResult(null);
    try {
      const body: Record<string, unknown> = {
        useOriginalWhenTranslatedEmpty: flatFillEmptyWithOriginal
      };
      if (useMirrorPath) {
        if (!siteHost.trim() || !version.trim()) {
          throw new Error("Choose a site host and version from the lists.");
        }
        body.siteHost = siteHost.trim();
        body.version = version.trim();
        body.pagePath = getExactlyOneSelectedPage();
      } else {
        if (!blockPageText.trim()) {
          throw new Error("Paste block page JSON or enable “Mirror on disk”.");
        }
        body.blockPageJson = blockPageText;
      }

      const res = await fetch("/api/mirror/block-exchange/to-flat", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          ...authHeaders()
        },
        body: JSON.stringify(body)
      });
      const data = (await res.json().catch(() => null)) as ToFlatResponse & { message?: string };
      if (!res.ok) {
        throw new Error(data?.message || `Export failed (${res.status}).`);
      }
      setFlatResult(data);
    } catch (err) {
      setFlatError(err instanceof Error ? err.message : "Export failed.");
    } finally {
      setFlatBusy(false);
    }
  }

  async function onPerPageFlatDownloads() {
    setBatchBusy(true);
    setBatchError(null);
    setPerPageFlatResults(null);
    try {
      if (!useMirrorPath) {
        throw new Error("Per-page flat export is only available in “Mirror on disk” mode.");
      }
      if (!siteHost.trim() || !version.trim()) {
        throw new Error("Choose a site host and version.");
      }
      const pathList = getMultiSelectedPages();
      if (pathList.length === 0) {
        throw new Error("Select one or more page paths (checkboxes).");
      }

      const hostSlug = siteHost.trim().replace(/\./g, "_");
      const verSlug = version.trim().replace(/[^\w.-]+/g, "_");
      const results: PerPageFlatExportRow[] = [];

      for (let i = 0; i < pathList.length; i++) {
        const pagePath = pathList[i]!;
        if (i > 0) {
          await new Promise((r) => setTimeout(r, 280));
        }

        const res = await fetch("/api/mirror/block-exchange/to-flat", {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            ...authHeaders()
          },
          body: JSON.stringify({
            siteHost: siteHost.trim(),
            version: version.trim(),
            pagePath,
            useOriginalWhenTranslatedEmpty: flatFillEmptyWithOriginal
          })
        });
        const data = (await res.json().catch(() => null)) as ToFlatResponse & { message?: string };
        if (!res.ok) {
          results.push({
            pagePath,
            ok: false,
            error: data?.message || `HTTP ${res.status}`
          });
          continue;
        }

        const entries = data.entries ?? {};
        const entryCount = Object.keys(entries).length;
        const json =
          data.entriesJson && data.entriesJson.length > 0
            ? data.entriesJson
            : JSON.stringify(entries, null, 2);
        const slug = flatDownloadSlug(pagePath);
        const fileName = `translations.flat.${hostSlug}.${verSlug}.${slug}.json`;
        downloadText(fileName, json, "application/json");
        results.push({ pagePath, ok: true, entryCount, fileName });
      }

      setPerPageFlatResults(results);
    } catch (err) {
      setBatchError(err instanceof Error ? err.message : "Export failed.");
    } finally {
      setBatchBusy(false);
    }
  }

  const mergeCount = mergeResult?.items?.length ?? 0;
  const unmatchedCount = mergeResult?.unmatchedTranslationKeys?.length ?? 0;
  const flatEntryCount = flatResult?.entries ? Object.keys(flatResult.entries).length : 0;

  return (
    <div className="block-exchange-page">
      <header className="block-exchange-hero card">
        <div className="block-exchange-hero-top">
          <span className="block-exchange-badge">i18n</span>
          <div>
            <h2 className="block-exchange-title">Block JSON exchange</h2>
            <p className="block-exchange-lede muted">
              Switch between a flat <code>{"{ \"source\": \"…\" }"}</code> map and a full{" "}
              <code>docs.json</code>. Keys can be block ids (<code>b_…</code>) or exact{" "}
              <code>Original</code> strings.
            </p>
          </div>
        </div>

        <div className="block-exchange-source">
          <div className="block-exchange-segment" role="group" aria-label="Template source">
            <button
              type="button"
              className={`block-exchange-segment-btn ${useMirrorPath ? "is-active" : ""}`}
              onClick={() => setUseMirrorPath(true)}
            >
              Mirror on disk
            </button>
            <button
              type="button"
              className={`block-exchange-segment-btn ${!useMirrorPath ? "is-active" : ""}`}
              onClick={() => setUseMirrorPath(false)}
            >
              Paste JSON
            </button>
          </div>

          {useMirrorPath ? (
            <div className="block-exchange-field">
              <div className="block-exchange-fields-3">
                <div className="block-exchange-field">
                  <label className="block-exchange-label" htmlFor="bx-host">
                    Site host
                  </label>
                  <select
                    id="bx-host"
                    className="block-exchange-select"
                    value={siteHost}
                    onChange={(e) => {
                      setSiteHost(e.target.value);
                      setVersion("");
                      setPages([]);
                      setSelectedPages({});
                    }}
                    disabled={catalogBusy && hosts.length === 0}
                  >
                    {hosts.length === 0 ? <option value="">—</option> : null}
                    {hosts.map((h) => (
                      <option key={h} value={h}>
                        {h}
                      </option>
                    ))}
                  </select>
                </div>
                <div className="block-exchange-field">
                  <label className="block-exchange-label" htmlFor="bx-version">
                    Version
                  </label>
                  <select
                    id="bx-version"
                    className="block-exchange-select"
                    value={version}
                    onChange={(e) => {
                      setVersion(e.target.value);
                      setSelectedPages({});
                    }}
                    disabled={!siteHost || versions.length === 0}
                  >
                    {versions.length === 0 ? <option value="">—</option> : null}
                    {versions.map((v) => (
                      <option key={v} value={v}>
                        {v}
                      </option>
                    ))}
                  </select>
                </div>
                <div className="block-exchange-field">
                  <label className="block-exchange-label">Mirror path</label>
                  <p className="muted" style={{ margin: 0, fontSize: "0.85rem" }}>
                    <code>
                      public/mirror/{siteHost || "…"}/{version || "…"}/_i18n/blocks/
                    </code>
                  </p>
                </div>
              </div>

              <label className="block-exchange-label" style={{ marginTop: "0.75rem", display: "block" }}>
                Block pages (<code>_i18n/blocks/*.json</code>, excluding <code>_common.json</code>)
              </label>
              <p className="muted block-exchange-hint" style={{ marginTop: "0.25rem" }}>
                Merge and <strong>Build flat JSON (one page)</strong> need <strong>exactly one</strong> checked page.
                <strong> Download flat JSON (each page)</strong> runs one export per checked page and saves a separate
                file (small delay between downloads so the browser keeps all of them).
              </p>
              <div className="block-exchange-page-toolbar">
                <button type="button" className="block-exchange-btn-secondary" onClick={selectAllPages} disabled={!pages.length}>
                  Select all
                </button>
                <button type="button" className="block-exchange-btn-secondary" onClick={clearPageSelection} disabled={!selectedCount}>
                  Clear
                </button>
                <span className="muted" style={{ fontSize: "0.85rem" }}>
                  {selectedCount} of {pages.length} selected
                </span>
              </div>
              <div className="block-exchange-page-panel" role="list">
                {!siteHost || !version ? (
                  <p className="muted">Choose host and version.</p>
                ) : pages.length === 0 ? (
                  <p className="muted">No block JSON files found for this mirror.</p>
                ) : (
                  pages.map((p) => (
                    <label key={p} className="block-exchange-page-row">
                      <input type="checkbox" checked={!!selectedPages[p]} onChange={() => togglePage(p)} />
                      <code>{p}</code>
                    </label>
                  ))
                )}
              </div>
              <div className="block-exchange-actions" style={{ marginTop: "0.75rem" }}>
                <button
                  type="button"
                  className="block-exchange-btn-primary"
                  disabled={batchBusy || selectedCount === 0}
                  onClick={() => void onPerPageFlatDownloads()}
                >
                  {batchBusy ? "Downloading…" : "Download flat JSON (each selected page)"}
                </button>
              </div>
              {batchError ? <p className="error block-exchange-alert">{batchError}</p> : null}
              {perPageFlatResults && perPageFlatResults.length > 0 ? (
                <div className="block-exchange-result" style={{ marginTop: "0.75rem" }}>
                  <div className="block-exchange-chips">
                    <span className="block-exchange-chip">
                      {perPageFlatResults.filter((r) => r.ok).length} downloaded
                    </span>
                    {perPageFlatResults.some((r) => !r.ok) ? (
                      <span className="block-exchange-chip block-exchange-chip--warn">
                        {perPageFlatResults.filter((r) => !r.ok).length} failed
                      </span>
                    ) : null}
                  </div>
                  <details className="block-exchange-details" open>
                    <summary>Per-page summary</summary>
                    <ul className="muted" style={{ margin: "0.5rem 0 0", paddingLeft: "1.25rem", fontSize: "0.88rem" }}>
                      {perPageFlatResults.map((r) => (
                        <li key={r.pagePath}>
                          <code>{r.pagePath}</code>
                          {r.ok ? (
                            <>
                              {" "}
                              → <code>{r.fileName}</code> ({r.entryCount} entries)
                            </>
                          ) : (
                            <>
                              {" "}
                              — <span className="error">{r.error}</span>
                            </>
                          )}
                        </li>
                      ))}
                    </ul>
                  </details>
                </div>
              ) : null}
            </div>
          ) : (
            <div className="block-exchange-field">
              <label className="block-exchange-label">Block page file</label>
              <label className="block-exchange-dropzone">
                <input
                  type="file"
                  accept=".json,application/json"
                  className="block-exchange-file-native"
                  onChange={(e) => {
                    const f = e.target.files?.[0];
                    if (!f) return;
                    void f.text().then(setBlockPageText);
                  }}
                />
                <span className="block-exchange-dropzone-text">
                  Drop a file or click to choose <strong>docs.json</strong>
                </span>
              </label>
              <textarea
                rows={8}
                value={blockPageText}
                onChange={(e) => setBlockPageText(e.target.value)}
                placeholder='{ "page": "/docs", "groups": [ … ] }'
                className="block-exchange-mono"
                aria-label="Block page JSON"
              />
            </div>
          )}
          {catalogError ? <p className="error block-exchange-alert">{catalogError}</p> : null}
        </div>
      </header>

      <div className="block-exchange-columns">
        <section className="block-exchange-card card">
          <div className="block-exchange-card-head block-exchange-card-head--merge">
            <h3>Flat map → docs.json</h3>
            <p className="block-exchange-card-desc">Apply translations and download merged blocks.</p>
          </div>
          <form onSubmit={onMergeSubmit} className="block-exchange-form">
            <label className="block-exchange-check">
              <input
                type="checkbox"
                checked={emptyUsesOriginal}
                onChange={(ev) => setEmptyUsesOriginal(ev.target.checked)}
              />
              <span>
                <strong>Checked:</strong> missing map keys, empty values, or empty <code>Translated</code> fall
                back to <code>Original</code>.{" "}
                <strong>Unchecked:</strong> those become empty <code>Translated</code>, including when{" "}
                <code>Translated</code> is still identical to <code>Original</code> (English placeholder).
              </span>
            </label>

            <div className="block-exchange-field">
              <label className="block-exchange-label">Translations JSON</label>
              <label className="block-exchange-dropzone block-exchange-dropzone--compact">
                <input
                  type="file"
                  accept=".json,application/json"
                  className="block-exchange-file-native"
                  onChange={(e) => {
                    const f = e.target.files?.[0];
                    if (!f) return;
                    void f.text().then(setTranslationsText);
                  }}
                />
                <span className="block-exchange-dropzone-text">Upload .json or edit below</span>
              </label>
              <textarea
                rows={10}
                value={translationsText}
                onChange={(e) => setTranslationsText(e.target.value)}
                className="block-exchange-mono"
              />
            </div>

            <div className="block-exchange-actions">
              <button type="submit" className="block-exchange-btn-primary" disabled={mergeBusy}>
                {mergeBusy ? "Merging…" : "Merge & preview"}
              </button>
            </div>
          </form>

          {mergeError && <p className="error block-exchange-alert">{mergeError}</p>}

          {mergeResult?.blockPageJson && (
            <div className="block-exchange-result">
              <div className="block-exchange-chips">
                <span className="block-exchange-chip">{mergeCount} blocks</span>
                {unmatchedCount > 0 && (
                  <span className="block-exchange-chip block-exchange-chip--warn">
                    {unmatchedCount} unmatched keys
                  </span>
                )}
              </div>
              <div className="block-exchange-actions block-exchange-actions--split">
                <button
                  type="button"
                  className="block-exchange-btn-secondary"
                  onClick={() =>
                    downloadText("docs.merged.json", mergeResult.blockPageJson!, "application/json")
                  }
                >
                  Download docs.merged.json
                </button>
              </div>
              {mergeResult.unmatchedTranslationKeys && mergeResult.unmatchedTranslationKeys.length > 0 && (
                <p className="muted block-exchange-hint">
                  Keys with no matching block:{" "}
                  <code className="block-exchange-code-inline">
                    {mergeResult.unmatchedTranslationKeys.slice(0, 6).join(", ")}
                    {mergeResult.unmatchedTranslationKeys.length > 6 ? "…" : ""}
                  </code>
                </p>
              )}
              <details className="block-exchange-details">
                <summary>Preview block list ({mergeCount})</summary>
                <pre className="block-exchange-pre">
                  {JSON.stringify(mergeResult.items?.slice(0, 40), null, 2)}
                  {(mergeResult.items?.length ?? 0) > 40 ? "\n…" : ""}
                </pre>
              </details>
            </div>
          )}
        </section>

        <section className="block-exchange-card card">
          <div className="block-exchange-card-head block-exchange-card-head--flat">
            <h3>docs.json → flat map</h3>
            <p className="block-exchange-card-desc">
              Export <code>Original</code> → <code>Translated</code> for spreadsheets or CAT tools.
            </p>
          </div>
          <form onSubmit={onFlatSubmit} className="block-exchange-form">
            <p className="muted block-exchange-hint">
              Uses the same template source as the card above (mirror path or pasted JSON). In mirror mode, pick{" "}
              <strong>one</strong> page checkbox here, or use <strong>Download flat JSON (each selected page)</strong> in
              the header to save one file per checked page.
            </p>
            <label className="block-exchange-check">
              <input
                type="checkbox"
                checked={flatFillEmptyWithOriginal}
                onChange={(ev) => setFlatFillEmptyWithOriginal(ev.target.checked)}
              />
              <span>
                <strong>Checked:</strong> empty <code>Translated</code> is exported as <code>Original</code>.{" "}
                <strong>Unchecked:</strong> export <code>""</code> when <code>Translated</code> is empty{" "}
                <em>or</em> still the same as <code>Original</code> (typical untranslated placeholder).
              </span>
            </label>
            <div className="block-exchange-actions">
              <button type="submit" className="block-exchange-btn-primary" disabled={flatBusy}>
                {flatBusy ? "Exporting…" : "Build flat JSON (one page)"}
              </button>
            </div>
          </form>

          {flatError && <p className="error block-exchange-alert">{flatError}</p>}

          {flatResult?.entriesJson && (
            <div className="block-exchange-result">
              <div className="block-exchange-chips">
                <span className="block-exchange-chip">{flatEntryCount} entries</span>
              </div>
              <div className="block-exchange-actions">
                <button
                  type="button"
                  className="block-exchange-btn-secondary"
                  onClick={() =>
                    downloadText("translations.flat.json", flatResult.entriesJson!, "application/json")
                  }
                >
                  Download translations.flat.json
                </button>
              </div>
              <details className="block-exchange-details">
                <summary>Preview</summary>
                <pre className="block-exchange-pre">
                  {JSON.stringify(flatResult.entries, null, 2)?.slice(0, 12_000)}
                </pre>
              </details>
            </div>
          )}
        </section>
      </div>
    </div>
  );
}
