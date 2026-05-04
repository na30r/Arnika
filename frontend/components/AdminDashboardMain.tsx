"use client";

import { FormEvent, useEffect, useMemo, useState } from "react";
import { useParams } from "next/navigation";
import { authHeaders } from "../lib/auth";
import { type Locale, t } from "../lib/i18n";
import { TranslationReviewSection } from "./TranslationReviewSection";
import { CrawledSitesSection } from "./CrawledSitesSection";
import { AssetInjectionSection } from "./AssetInjectionSection";
import { BlockExchangeSection } from "./BlockExchangeSection";
import { LogsSection } from "./LogsSection";
import { TranslationDbSection } from "./TranslationDbSection";
import Link from "next/link";

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

type MirrorQueueItemDto = {
  itemId?: string;
  url?: string;
  status?: string;
  crawlId?: string | null;
  errorMessage?: string | null;
  result?: MirrorResponse | null;
};

type MirrorQueueBatchPayload = {
  batchId?: string;
  items?: MirrorQueueItemDto[];
  allFinished?: boolean;
  purgedFromDatabase?: boolean;
};

const defaultTarget = "https://nextjs.org/docs";

function parseDocumentationUrls(text: string): string[] {
  return text
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

function batchLabelSuffix(batch: MirrorQueueBatchPayload): string {
  if (!batch.batchId) {
    return "";
  }
  const tail = batch.purgedFromDatabase ? " — removed from queue table" : "";
  return `: ${batch.batchId}${tail}`;
}

export function AdminDashboardMain() {
  const params = useParams();
  const locale = (params?.locale as Locale) || "en";
  const [urlsText, setUrlsText] = useState(defaultTarget);
  const [version, setVersion] = useState("latest");
  const [linkDrillCount, setLinkDrillCount] = useState(0);
  const [languagesText, setLanguagesText] = useState("en,fa");
  const [doNotTranslateText, setDoNotTranslateText] = useState("");
  const [generalClassesText, setGeneralClassesText] = useState("");
  const [crawlAllowPrefixesText, setCrawlAllowPrefixesText] = useState("");
  const [crawlDenyPrefixesText, setCrawlDenyPrefixesText] = useState("");
  const [siteOrigin, setSiteOrigin] = useState("");
  const [selectedLanguage, setSelectedLanguage] = useState("en");
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<MirrorResponse | null>(null);
  const [queueBatchId, setQueueBatchId] = useState<string | null>(null);
  const [queueBatchSnapshot, setQueueBatchSnapshot] = useState<MirrorQueueBatchPayload | null>(null);
  const [queuePollActive, setQueuePollActive] = useState(false);
  const [queueError, setQueueError] = useState<string | null>(null);
  const [manualPreviewPath, setManualPreviewPath] = useState("/mirror/nextjs.org/latest/_localized/en/docs.html");
  const [activeSection, setActiveSection] = useState<
    "crawl" | "translations" | "blockExchange" | "crawledSites" | "assetInjection" | "logs" | "translationDb"
  >("crawl");

  useEffect(() => {
    setSiteOrigin(typeof window !== "undefined" ? window.location.origin : "");
  }, []);

  useEffect(() => {
    if (!queueBatchId || !queuePollActive) {
      return;
    }

    let cancelled = false;

    async function pollOnce() {
      try {
        const response = await fetch(`/api/mirror/queue/batch/${encodeURIComponent(queueBatchId!)}`, {
          headers: { ...authHeaders() },
          cache: "no-store"
        });
        const text = await response.text();
        let payload: MirrorQueueBatchPayload | null = null;
        try {
          payload = text ? (JSON.parse(text) as MirrorQueueBatchPayload) : null;
        } catch {
          payload = null;
        }
        if (!response.ok) {
          if (cancelled) return;
          const hint =
            response.status === 404
              ? " If the API is crawling but this says not found, Next.js MIRROR_API_BASE_URL may point at a different server than the one that received the enqueue."
              : "";
          setQueueError(
            ((payload as { message?: string } | null)?.message || `Batch status failed (${response.status}).`) + hint
          );
          setQueuePollActive(false);
          return;
        }
        if (cancelled) return;
        setQueueError(null);
        setQueueBatchSnapshot(payload);
        if (payload?.allFinished) {
          setQueuePollActive(false);
          const firstOk = payload.items?.find(
            (i) => i.status === "completed" && i.result?.frontendPreviewPath
          );
          if (firstOk?.result) {
            setResult(firstOk.result);
            if (firstOk.result.frontendPreviewPath) {
              setManualPreviewPath(firstOk.result.frontendPreviewPath);
            }
            if (firstOk.result.defaultLanguage) {
              setSelectedLanguage(firstOk.result.defaultLanguage);
            }
            if (firstOk.result.availableLanguages?.length) {
              setLanguagesText(firstOk.result.availableLanguages.join(", "));
            }
          }
        }
      } catch {
        if (!cancelled) {
          setQueueError("Could not reach batch status API.");
        }
      }
    }

    const interval = window.setInterval(pollOnce, 1000);
    pollOnce();
    return () => {
      cancelled = true;
      window.clearInterval(interval);
    };
  }, [queueBatchId, queuePollActive]);

  const manualPreviewFullUrl = useMemo(() => {
    const path = manualPreviewPath.trim();
    if (!path) return "";
    if (path.startsWith("http://") || path.startsWith("https://")) return path;
    if (!siteOrigin) return path;
    return `${siteOrigin}${path.startsWith("/") ? path : `/${path}`}`;
  }, [manualPreviewPath, siteOrigin]);

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
    setQueueError(null);

    const docUrls = parseDocumentationUrls(urlsText);
    if (docUrls.length === 0) {
      setIsLoading(false);
      setError("Enter at least one documentation URL.");
      return;
    }

    try {
      const parsedLanguages = languagesText
        .split(",")
        .map((item) => item.trim().toLowerCase())
        .filter(Boolean);
      const parsedDoNotTranslateTexts = doNotTranslateText
        .split("\n")
        .map((item) => item.trim())
        .filter(Boolean);
      const parsedGeneralClasses = generalClassesText
        .split("\n")
        .map((item) => item.trim())
        .filter(Boolean);
      const parsedAllowPrefixes = crawlAllowPrefixesText
        .split("\n")
        .map((item) => item.trim())
        .filter(Boolean);
      const parsedDenyPrefixes = crawlDenyPrefixesText
        .split("\n")
        .map((item) => item.trim())
        .filter(Boolean);

      const mirrorBodyBase = {
        version,
        linkDrillCount,
        languages: parsedLanguages.length > 0 ? parsedLanguages : ["en"],
        crawlUrlAllowPrefixes: parsedAllowPrefixes.length > 0 ? parsedAllowPrefixes : undefined,
        crawlUrlDenyPrefixes: parsedDenyPrefixes.length > 0 ? parsedDenyPrefixes : undefined,
        doNotTranslateTexts: parsedDoNotTranslateTexts,
        generalTranslationClasses: parsedGeneralClasses,
        extraWaitMs: 4000,
        autoScroll: true,
        scrollStepPx: 1200,
        scrollDelayMs: 150,
        maxScrollRounds: 24
      };

      if (docUrls.length >= 2) {
        setQueueBatchId(null);
        setQueueBatchSnapshot(null);
        setQueuePollActive(false);

        const response = await fetch("/api/mirror/queue", {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            ...authHeaders()
          },
          body: JSON.stringify({
            urls: docUrls,
            ...mirrorBodyBase
          })
        });

        const text = await response.text();
        let payload: { batchId?: string; message?: string } | null = null;
        try {
          payload = text ? (JSON.parse(text) as { batchId?: string; message?: string }) : null;
        } catch {
          throw new Error(`Mirror queue API error (${response.status}). ${text.slice(0, 200)}`);
        }
        if (!response.ok) {
          throw new Error(payload?.message || `Mirror queue request failed (${response.status}).`);
        }
        if (!payload?.batchId) {
          throw new Error("Queue did not return a batch id.");
        }

        setQueueBatchId(payload.batchId);
        setQueueBatchSnapshot({
          batchId: payload.batchId,
          items: docUrls.map((urlLine, i) => ({
            itemId: `pending-${i}`,
            url: urlLine,
            status: "pending",
            crawlId: null,
            errorMessage: null,
            result: null
          })),
          allFinished: false,
          purgedFromDatabase: false
        });
        setQueuePollActive(true);
        setIsLoading(false);
        return;
      }

      const response = await fetch("/api/mirror", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          ...authHeaders()
        },
        body: JSON.stringify({
          url: docUrls[0],
          ...mirrorBodyBase
        })
      });

      const text = await response.text();
      let payload: (MirrorResponse & { message?: string; hint?: string }) | null = null;
      try {
        payload = text ? (JSON.parse(text) as MirrorResponse & { message?: string; hint?: string }) : null;
      } catch {
        throw new Error(`Mirror API error (${response.status}). ${text.slice(0, 200)}`);
      }
      if (!response.ok) {
        throw new Error(payload?.message || `Mirror request failed (${response.status}).`);
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
    <main className="page admin-layout">
      <aside className="card admin-sidebar">
        <h3>Admin</h3>
        <button
          type="button"
          className={`sidebar-item ${activeSection === "crawl" ? "active" : ""}`}
          onClick={() => setActiveSection("crawl")}
        >
          Crawl & Preview
        </button>
        <button
          type="button"
          className={`sidebar-item ${activeSection === "translations" ? "active" : ""}`}
          onClick={() => setActiveSection("translations")}
        >
          Translation Review
        </button>
        <button
          type="button"
          className={`sidebar-item ${activeSection === "blockExchange" ? "active" : ""}`}
          onClick={() => setActiveSection("blockExchange")}
        >
          Block JSON exchange
        </button>
        <button
          type="button"
          className={`sidebar-item ${activeSection === "crawledSites" ? "active" : ""}`}
          onClick={() => setActiveSection("crawledSites")}
        >
          Crawled Sites
        </button>
        <button
          type="button"
          className={`sidebar-item ${activeSection === "assetInjection" ? "active" : ""}`}
          onClick={() => setActiveSection("assetInjection")}
        >
          Asset Injection
        </button>
        <button
          type="button"
          className={`sidebar-item ${activeSection === "logs" ? "active" : ""}`}
          onClick={() => setActiveSection("logs")}
        >
          Server logs
        </button>
        <button
          type="button"
          className={`sidebar-item ${activeSection === "translationDb" ? "active" : ""}`}
          onClick={() => setActiveSection("translationDb")}
        >
          Translation DB
        </button>
        <p className="muted small-note" style={{ marginTop: "0.5rem" }}>
          <Link href={`/${locale}/admin/translation-db`}>Open Translation DB full page →</Link>
        </p>
      </aside>

      <section className="admin-content">
      {activeSection === "crawl" ? (
      <>
      <section className="card controls">
        <h1>{t("home.title", locale)}</h1>
        <p className="muted">{t("home.subtitle", locale)}</p>

        <form onSubmit={handleSubmit} className="form">
          <label htmlFor="url-input">{t("form.url", locale)}</label>
          <p className="muted small-note">
            One URL mirrors immediately in the browser request. Two or more lines enqueue jobs in SQL; the API runs
            them <strong>one after another</strong> (shared disk output cannot be mirrored in parallel on Windows). This
            page polls for per-URL status. When every URL in the batch has finished, the next status poll returns the
            final snapshot and rows are deleted from the queue table.
          </p>
          <textarea
            id="url-input"
            value={urlsText}
            onChange={(event) => setUrlsText(event.target.value)}
            placeholder={"https://nextjs.org/docs\nhttps://nextjs.org/docs/app"}
            autoComplete="off"
            required
            rows={5}
            className="urls-textarea"
          />
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
          <label htmlFor="crawl-allow-input">Crawl URL allow prefixes (optional, one per line)</label>
          <p className="muted small-note">
            If set, link drill only follows same-site URLs under these prefixes (e.g.{" "}
            <code>https://nextjs.org/docs</code> or <code>/docs</code>).
          </p>
          <textarea
            id="crawl-allow-input"
            value={crawlAllowPrefixesText}
            onChange={(event) => setCrawlAllowPrefixesText(event.target.value)}
            placeholder={"https://nextjs.org/docs\n/docs/app"}
            rows={3}
          />
          <label htmlFor="crawl-deny-input">Crawl URL deny prefixes (optional, one per line)</label>
          <p className="muted small-note">
            Same-site URLs under these paths are skipped (e.g. <code>/blog</code> excludes blog and child
            pages).
          </p>
          <textarea
            id="crawl-deny-input"
            value={crawlDenyPrefixesText}
            onChange={(event) => setCrawlDenyPrefixesText(event.target.value)}
            placeholder={"/blog\nhttps://nextjs.org/showcase"}
            rows={3}
          />
          <label htmlFor="languages-input">{t("form.languages", locale)}</label>
          <div className="row">
            <input
              id="languages-input"
              value={languagesText}
              onChange={(event) => setLanguagesText(event.target.value)}
              placeholder="en, fa"
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
          <label htmlFor="general-classes-input">General Translation Classes (one per line)</label>
          <textarea
            id="general-classes-input"
            value={generalClassesText}
            onChange={(event) => setGeneralClassesText(event.target.value)}
            placeholder={"navbar-module__cV3TuW__nav\nfooter\nshared-banner"}
            rows={4}
          />
        </form>

        {error && <p className="error">{error}</p>}
        {queueError && <p className="error">{queueError}</p>}

        {queueBatchSnapshot?.items && queueBatchSnapshot.items.length > 0 && (
          <div className="form queue-batch-panel">
            <label>Queued mirrors{batchLabelSuffix(queueBatchSnapshot)}</label>
            <div className="queue-batch-table" role="table">
              <div className="queue-batch-head" role="row">
                <span role="columnheader">URL</span>
                <span role="columnheader">Status</span>
                <span role="columnheader">Result</span>
              </div>
              {queueBatchSnapshot.items.map((item, index) => (
                <div className="queue-batch-row" role="row" key={item.itemId ?? `${item.url}-${index}`}>
                  <span className="queue-batch-url" role="cell">
                    {item.url}
                  </span>
                  <span role="cell">
                    <strong>{item.status ?? "-"}</strong>
                    {item.crawlId ? (
                      <span className="muted small-note" style={{ display: "block" }}>
                        <code>{item.crawlId}</code>
                      </span>
                    ) : null}
                    {item.errorMessage ? (
                      <span className="error small-note" style={{ display: "block" }}>
                        {item.errorMessage}
                      </span>
                    ) : null}
                  </span>
                  <span role="cell" className="queue-batch-actions">
                    {item.result?.frontendPreviewPath ? (
                      <button
                        type="button"
                        className="queue-item"
                        onClick={() => {
                          setManualPreviewPath(item.result!.frontendPreviewPath!);
                          setResult(item.result!);
                        }}
                      >
                        Preview
                      </button>
                    ) : (
                      <span className="muted">—</span>
                    )}
                    {item.result?.filesSaved != null ? (
                      <span className="muted small-note" style={{ display: "block" }}>
                        {item.result.filesSaved} files
                      </span>
                    ) : null}
                  </span>
                </div>
              ))}
            </div>
          </div>
        )}

        {(queueBatchId || queuePollActive) && (
          <p className="muted small-note">
            Queue batch: <code>{queueBatchId ?? "…"}</code>
            {queuePollActive ? " — polling status every second" : ""}
          </p>
        )}

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
          {manualPreviewFullUrl ? (
            <p className="manual-preview-full-url">
              <span className="muted">Full URL: </span>
              <a href={manualPreviewFullUrl} target="_blank" rel="noreferrer">
                {manualPreviewFullUrl}
              </a>
            </p>
          ) : null}
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
      </>
      ) : activeSection === "translations" ? (
        <TranslationReviewSection />
      ) : activeSection === "blockExchange" ? (
        <BlockExchangeSection />
      ) : activeSection === "crawledSites" ? (
        <CrawledSitesSection />
      ) : activeSection === "logs" ? (
        <LogsSection />
      ) : activeSection === "translationDb" ? (
        <TranslationDbSection embedded />
      ) : (
        <AssetInjectionSection />
      )}
      </section>
    </main>
  );
}
