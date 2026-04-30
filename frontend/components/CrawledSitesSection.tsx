"use client";

import { useState } from "react";
import { authHeaders } from "../lib/auth";

type CrawledPage = {
  entryFileRelativePath?: string;
  frontendPreviewPath?: string;
};

type CrawledRun = {
  crawlId?: string;
  status?: string;
  createdAtUtc?: string;
  processedPages?: number;
  totalFilesSaved?: number;
};

type CrawledSite = {
  siteHost?: string;
  version?: string;
  pages?: CrawledPage[];
  crawlRuns?: CrawledRun[];
};

export function CrawledSitesSection() {
  const [sites, setSites] = useState<CrawledSite[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function loadSites() {
    setLoading(true);
    setError(null);
    try {
      const response = await fetch("/api/mirror/sites", {
        method: "GET",
        headers: { ...authHeaders() },
        cache: "no-store"
      });
      const text = await response.text();
      const payload = text ? (JSON.parse(text) as CrawledSite[] | { message?: string }) : [];
      if (!response.ok) {
        const message = !Array.isArray(payload) ? payload.message : "Failed to load crawled sites.";
        throw new Error(message || `Request failed (${response.status}).`);
      }
      setSites(Array.isArray(payload) ? payload : []);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Failed to load crawled sites.");
      setSites([]);
    } finally {
      setLoading(false);
    }
  }

  return (
    <section className="card controls">
      <h2>Crawled Sites</h2>
      <p className="muted">View crawled hosts/versions and open preview pages.</p>
      <div className="row">
        <button type="button" onClick={loadSites} disabled={loading}>
          {loading ? "Loading..." : "Refresh Sites"}
        </button>
      </div>
      {error && <p className="error">{error}</p>}

      {sites.length === 0 ? (
        <p className="muted">No crawled sites loaded yet.</p>
      ) : (
        <div className="queue-list">
          {sites.map((site) => (
            <div key={`${site.siteHost}-${site.version}`} className="card" style={{ marginBottom: 12 }}>
              <div className="meta">
                <div><strong>Site:</strong> {site.siteHost || "-"}</div>
                <div><strong>Version:</strong> {site.version || "-"}</div>
                <div><strong>Runs:</strong> {site.crawlRuns?.length ?? 0}</div>
                <div><strong>Pages:</strong> {site.pages?.length ?? 0}</div>
              </div>
              {site.pages && site.pages.length > 0 && (
                <div className="form">
                  <label>Preview Pages</label>
                  <div className="queue-list">
                    {site.pages.map((page) => (
                      <a
                        key={page.entryFileRelativePath || page.frontendPreviewPath}
                        href={page.frontendPreviewPath || "#"}
                        target="_blank"
                        rel="noreferrer"
                        className="queue-item"
                      >
                        {page.entryFileRelativePath || page.frontendPreviewPath}
                      </a>
                    ))}
                  </div>
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </section>
  );
}
