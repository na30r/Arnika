"use client";

import { FormEvent, useState } from "react";
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

function downloadText(filename: string, text: string, mime: string) {
  const blob = new Blob([text], { type: mime });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

export function BlockExchangeSection() {
  const [siteHost, setSiteHost] = useState("nextjs.org");
  const [version, setVersion] = useState("16.2.4");
  const [pagePath, setPagePath] = useState("docs");
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
  /** When true, empty Translated becomes Original in the flat file; when false, values stay "". */
  const [flatFillEmptyWithOriginal, setFlatFillEmptyWithOriginal] = useState(true);

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
        body.siteHost = siteHost.trim();
        body.version = version.trim();
        body.pagePath = pagePath.trim();
      } else {
        if (!blockPageText.trim()) {
          throw new Error("Paste block page JSON or enable “Load from mirror path”.");
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
        body.siteHost = siteHost.trim();
        body.version = version.trim();
        body.pagePath = pagePath.trim();
      } else {
        if (!blockPageText.trim()) {
          throw new Error("Paste block page JSON or enable “Load from mirror path”.");
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
            <div className="block-exchange-fields-3">
              <div className="block-exchange-field">
                <label className="block-exchange-label" htmlFor="bx-host">
                  Site host
                </label>
                <input id="bx-host" value={siteHost} onChange={(e) => setSiteHost(e.target.value)} />
              </div>
              <div className="block-exchange-field">
                <label className="block-exchange-label" htmlFor="bx-version">
                  Version
                </label>
                <input id="bx-version" value={version} onChange={(e) => setVersion(e.target.value)} />
              </div>
              <div className="block-exchange-field">
                <label className="block-exchange-label" htmlFor="bx-page">
                  Page path
                </label>
                <input
                  id="bx-page"
                  value={pagePath}
                  onChange={(e) => setPagePath(e.target.value)}
                  placeholder="docs"
                />
              </div>
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
              Uses the same template source as the card above (mirror path or pasted JSON).
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
                {flatBusy ? "Exporting…" : "Build flat JSON"}
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
