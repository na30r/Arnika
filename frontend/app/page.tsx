"use client";

import { FormEvent, useMemo, useState } from "react";

type MirrorResponse = {
  sourceUrl?: string;
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
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<MirrorResponse | null>(null);
  const [manualPreviewPath, setManualPreviewPath] = useState("/mirror/nextjs.org/docs.html");

  const iframeSrc = useMemo(() => {
    if (result?.frontendPreviewPath) {
      return result.frontendPreviewPath;
    }

    return manualPreviewPath.trim();
  }, [manualPreviewPath, result?.frontendPreviewPath]);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setIsLoading(true);
    setError(null);

    try {
      const response = await fetch("/api/mirror", {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          url,
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
            <button type="submit" disabled={isLoading}>
              {isLoading ? "Mirroring..." : "Mirror page"}
            </button>
          </div>
        </form>

        {error && <p className="error">{error}</p>}

        <div className="meta">
          <div>
            <strong>Preview path:</strong>{" "}
            <code>{result?.frontendPreviewPath ?? "(none yet)"}</code>
          </div>
          <div>
            <strong>Files saved:</strong> {result?.filesSaved ?? 0}
          </div>
          <div>
            <strong>Final URL:</strong> {result?.finalUrl ?? "-"}
          </div>
        </div>

        <div className="form">
          <label htmlFor="manual-preview-input">Open existing mirrored page</label>
          <input
            id="manual-preview-input"
            value={manualPreviewPath}
            onChange={(event) => setManualPreviewPath(event.target.value)}
            placeholder="/mirror/nextjs.org/docs.html"
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
