"use client";

import { FormEvent, useState } from "react";
import { useParams } from "next/navigation";
import { authHeaders } from "../../../lib/auth";
import { type Locale, t } from "../../../lib/i18n";

type MirrorResponse = {
  crawlId?: string;
  sourceUrl?: string;
  siteHost?: string;
  version?: string;
  defaultLanguage?: string;
  availableLanguages?: string[];
  processedPages?: number;
  requestedLinkLimit?: number;
  pages?: {
    url?: string;
    finalUrl?: string;
    frontendPreviewPath?: string;
    entryFileRelativePath?: string;
    filesSaved?: number;
    pageStatus?: string;
  }[];
  finalUrl?: string;
  frontendPreviewPath?: string;
  filesSaved?: number;
  waitMs?: number;
  skippedPages?: number;
};

const defaultTarget = "https://nextjs.org/docs";

export default function HomePage() {
  const params = useParams();
  const locale = (params?.locale as Locale) || "en";
  const [url, setUrl] = useState(defaultTarget);
  const [version, setVersion] = useState("latest");
  const [linkDrillCount, setLinkDrillCount] = useState(0);
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
          "Content-Type": "application/json",
          ...authHeaders()
        },
        body: JSON.stringify({
          url,
          version,
          linkDrillCount,
          languages: parsedLanguages.length > 0 ? parsedLanguages : ["en"],
          doNotTranslateTexts: parsedDoNotTranslateTexts,
          extraWaitMs: 4000,
          autoScroll: true,
          scrollStepPx: 1200,
          scrollDelayMs: 150,
          maxScrollRounds: 24
        })
      });

      const text = await response.text();
      let payload: (MirrorResponse & { message?: string; hint?: string }) | null = null;
      try {
        payload = text ? (JSON.parse(text) as typeof payload) : null;
      } catch {
        throw new Error(`Mirror API error (${response.status}). ${text.slice(0, 200)}`);
      }
      if (!response.ok) {
        throw new Error(payload?.message ?? `Mirror request failed (${response.status}).`);
      }
      if (!payload) {
        throw new Error("Empty response from mirror API.");
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
        <h1>{t("home.title", locale)}</h1>
        <p className="muted">{t("home.subtitle", locale)}</p>

        <form onSubmit={handleSubmit} className="form">
          <label htmlFor="url-input">{t("form.url", locale)}</label>
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
          <label htmlFor="version-input">{t("form.version", locale)}</label>
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
              {isLoading ? t("form.mirroring", locale) : t("form.mirror", locale)}
            </button>
          </div>
          <label htmlFor="link-drill-input">{t("form.linkDrill", locale)}</label>
          <div className="row">
            <input
              id="link-drill-input"
              type="number"
              min={0}
              value={linkDrillCount}
              onChange={(event) => setLinkDrillCount(Number.isNaN(event.target.valueAsNumber) ? 0 : Math.max(0, Math.floor(event.target.valueAsNumber)))}
            />
          </div>
          <p className="muted small-note">{t("form.linkDrillHint", locale)}</p>
          <label htmlFor="languages-input">{t("form.languages", locale)}</label>
          <div className="row">
            <input
              id="languages-input"
              value={languagesText}
              onChange={(event) => setLanguagesText(event.target.value)}
              placeholder="en, fa, ar"
              autoComplete="off"
            />
          </div>
          <label htmlFor="do-not-translate-input">{t("form.doNotTranslate", locale)}</label>
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
            <strong>Crawl ID:</strong> <code>{result?.crawlId ?? "-"}</code>
          </div>
          <div>
            <strong>Preview path:</strong> <code>{result?.frontendPreviewPath ?? "(none yet)"}</code>
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
            <strong>Processed pages:</strong> {result?.processedPages ?? result?.pages?.length ?? 0}
          </div>
          <div>
            <strong>Skipped pages:</strong> {result?.skippedPages ?? 0}
          </div>
          <div>
            <strong>Requested link drill count:</strong> {result?.requestedLinkLimit ?? linkDrillCount}
          </div>
          <div>
            <strong>Final URL:</strong> {result?.finalUrl ?? "-"}
          </div>
        </div>

        <div className="form">
          <label htmlFor="language-select-input">{t("form.previewLanguage", locale)}</label>
          <input
            id="language-select-input"
            value={selectedLanguage}
            onChange={(event) => setSelectedLanguage(event.target.value.toLowerCase())}
            placeholder="en"
            autoComplete="off"
          />
          <label htmlFor="manual-preview-input">{t("form.openExisting", locale)}</label>
          <input
            id="manual-preview-input"
            value={manualPreviewPath}
            onChange={(event) => setManualPreviewPath(event.target.value)}
            placeholder="/mirror/nextjs.org/latest/_localized/en/docs.html"
            autoComplete="off"
          />
        </div>

        {result?.pages && result.pages.length > 0 && (
          <div className="form">
            <label>Pages</label>
            <div className="queue-list">
              {result.pages.map((page, index) => (
                <button
                  key={`${page.finalUrl ?? page.url ?? index}-${index}`}
                  type="button"
                  className="queue-item"
                  onClick={() => {
                    if (page.frontendPreviewPath) {
                      setManualPreviewPath(page.frontendPreviewPath);
                    }
                  }}
                >
                  <strong>{index + 1}.</strong> {page.finalUrl ?? page.url}{" "}
                  {page.pageStatus ? <span className="muted">({page.pageStatus})</span> : null}
                </button>
              ))}
            </div>
          </div>
        )}
      </section>

      <section className="card viewer">
        <div className="viewer-header">
          <h2>{t("preview.heading", locale)}</h2>
          <a href={iframeSrc} target="_blank" rel="noreferrer">
            {t("preview.openTab", locale)}
          </a>
        </div>
        <iframe key={iframeSrc} src={iframeSrc} title="Mirrored page preview" />
      </section>
    </main>
  );
}
