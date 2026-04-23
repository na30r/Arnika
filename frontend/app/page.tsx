"use client";

import { FormEvent, useState } from "react";

type MirrorResponse = {
  sourceUrl?: string;
  siteHost?: string;
  version?: string;
  defaultLanguage?: string;
  availableLanguages?: string[];
  finalUrl?: string;
  outputFolder?: string;
  entryFilePath?: string;
  entryFileRelativePath?: string;
  frontendPreviewPath?: string;
  filesSaved?: number;
  waitMs?: number;
};

const defaultTarget = "https://nextjs.org/docs";

export default function HomePage() {
  const [url, setUrl] = useState(defaultTarget);
  const [version, setVersion] = useState("latest");
  const [languagesText, setLanguagesText] = useState("en");
  const [doNotTranslateText, setDoNotTranslateText] = useState("");
  const [selectedLanguage, setSelectedLanguage] = useState("en");
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<MirrorResponse | null>(null);
  const [manualPreviewPath, setManualPreviewPath] = useState("/mirror/nextjs.org/latest/_localized/en/docs.html");

  let iframeSrc = manualPreviewPath.trim();
  if (result?.frontendPreviewPath && result?.siteHost && result?.version) {
    const effectiveLanguage = (selectedLanguage || result.defaultLanguage || "en").trim().toLowerCase();
    if (effectiveLanguage && result.defaultLanguage) {
      const basePrefix = `/mirror/${result.siteHost}/${result.version}/_localized/${result.defaultLanguage.toLowerCase()}/`;
      if (result.frontendPreviewPath.startsWith(basePrefix)) {
        iframeSrc = result.frontendPreviewPath.replace(
          basePrefix,
          `/mirror/${result.siteHost}/${result.version}/_localized/${effectiveLanguage}/`
        );
      } else {
        iframeSrc = result.frontendPreviewPath;
      }
    } else {
      iframeSrc = result.frontendPreviewPath;
    }
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setIsLoading(true);
    setError(null);

    try {
      const parsedLanguages = languagesText
        .split(",")
        .map((item) => item.trim().toLowerCase())
        .filter(Boolean);
      const parsedDoNotTranslateTexts = doNotTranslateText
        .split("\n")
        .map((item) => item.trim())
        .filter(Boolean);
      const response = await fetch("/api/mirror", {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          url,
          version,
          languages: parsedLanguages.length > 0 ? parsedLanguages : ["en"],
          doNotTranslateTexts: parsedDoNotTranslateTexts,
          extraWaitMs: 4000,
          autoScroll: true,
          scrollStepPx: 1200,
          scrollDelayMs: 150,
          maxScrollRounds: 24
        })
      });

      const payload = (await response.json()) as MirrorResponse & { message?: string };
      if (!response.ok) {
        throw new Error(payload.message ?? "Mirror request failed.");
      }

      setResult(payload);
      if (payload.frontendPreviewPath) {
        setManualPreviewPath(payload.frontendPreviewPath);
      }
      if (payload.defaultLanguage) {
        setSelectedLanguage(payload.defaultLanguage);
      }
      if (payload.availableLanguages && payload.availableLanguages.length > 0) {
        setLanguagesText(payload.availableLanguages.join(", "));
      }
    } catch (caught) {
      const message = caught instanceof Error ? caught.message : "Unexpected error.";
      setError(message);
    } finally {
      setIsLoading(false);
    }
  }

  return (
    <main className="page">
      <section className="card controls">
        <h1>Documentation Mirror (Single Page)</h1>
        <p className="muted">
          Crawl one documentation page (including Next.js/Tailwind-rendered assets) and preview it locally from this app.
        </p>

        <form onSubmit={handleSubmit} className="form">
          <label htmlFor="url-input">Documentation page URL</label>
          <div className="row">
            <input
              id="url-input"
              value={url}
              onChange={(event) => setUrl(event.target.value)}
              placeholder="https://nextjs.org/docs"
              autoComplete="off"
              required
            />
          </div>
          <label htmlFor="version-input">Version</label>
          <div className="row">
            <input
              id="version-input"
              value={version}
              onChange={(event) => setVersion(event.target.value)}
              placeholder="latest, 14.2.0, v1"
              autoComplete="off"
              required
            />
            <button type="submit" disabled={isLoading}>
              {isLoading ? "Mirroring..." : "Mirror page"}
            </button>
          </div>
          <label htmlFor="languages-input">Languages (comma-separated)</label>
          <div className="row">
            <input
              id="languages-input"
              value={languagesText}
              onChange={(event) => setLanguagesText(event.target.value)}
              placeholder="en, fa, ar"
              autoComplete="off"
            />
          </div>
          <label htmlFor="do-not-translate-input">Do not translate texts (one per line)</label>
          <textarea
            id="do-not-translate-input"
            value={doNotTranslateText}
            onChange={(event) => setDoNotTranslateText(event.target.value)}
            placeholder={"API\nHTTP\nNext.js"}
            rows={5}
          />
        </form>

        {error && <p className="error">{error}</p>}

        <div className="meta">
          <div>
            <strong>Preview path:</strong>{" "}
            <code>{result?.frontendPreviewPath ?? "(none yet)"}</code>
          </div>
          <div>
            <strong>Site:</strong> {result?.siteHost ?? "-"}
          </div>
          <div>
            <strong>Version:</strong> {result?.version ?? "-"}
          </div>
          <div>
            <strong>Default language:</strong> {result?.defaultLanguage ?? "-"}
          </div>
          <div>
            <strong>Available languages:</strong> {(result?.availableLanguages ?? []).join(", ") || "-"}
          </div>
          <div>
            <strong>Files saved:</strong> {result?.filesSaved ?? 0}
          </div>
          <div>
            <strong>Final URL:</strong> {result?.finalUrl ?? "-"}
          </div>
        </div>

        <div className="form">
          <label htmlFor="language-select-input">Preview language</label>
          <input
            id="language-select-input"
            value={selectedLanguage}
            onChange={(event) => setSelectedLanguage(event.target.value.toLowerCase())}
            placeholder="en"
            autoComplete="off"
          />
          <label htmlFor="manual-preview-input">Open existing mirrored page</label>
          <input
            id="manual-preview-input"
            value={manualPreviewPath}
            onChange={(event) => setManualPreviewPath(event.target.value)}
            placeholder="/mirror/nextjs.org/latest/_localized/en/docs.html"
            autoComplete="off"
          />
        </div>
      </section>

      <section className="card viewer">
        <div className="viewer-header">
          <h2>Mirrored page preview</h2>
          <a href={iframeSrc} target="_blank" rel="noreferrer">
            Open in new tab
          </a>
        </div>
        <iframe key={iframeSrc} src={iframeSrc} title="Mirrored page preview" />
      </section>
    </main>
  );
}
