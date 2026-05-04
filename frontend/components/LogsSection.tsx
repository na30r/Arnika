"use client";

import { useCallback, useEffect, useState } from "react";
import { authHeaders } from "../lib/auth";

type LogLine = {
  timestampIso?: string;
  level?: string;
  message?: string;
  exception?: string | null;
  properties?: Record<string, string>;
};

const levels = ["", "Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

function levelClass(level: string | undefined): string {
  const u = (level ?? "").toLowerCase();
  if (u === "error" || u === "fatal") return "log-level log-level-danger";
  if (u === "warning") return "log-level log-level-warn";
  if (u === "information") return "log-level log-level-info";
  return "log-level log-level-muted";
}

export function LogsSection() {
  const [lines, setLines] = useState<LogLine[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [level, setLevel] = useState("");
  const [take, setTake] = useState(500);
  const [autoRefresh, setAutoRefresh] = useState(true);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const params = new URLSearchParams();
      params.set("take", String(Math.min(5000, Math.max(1, take))));
      if (level) params.set("level", level);
      const response = await fetch(`/api/logs?${params.toString()}`, {
        method: "GET",
        headers: { ...authHeaders() },
        cache: "no-store"
      });
      const text = await response.text();
      const payload = text ? (JSON.parse(text) as LogLine[] | { message?: string }) : [];
      if (!response.ok) {
        const message = Array.isArray(payload) ? `Request failed (${response.status}).` : payload.message;
        throw new Error(message || `Request failed (${response.status}).`);
      }
      setLines(Array.isArray(payload) ? payload : []);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Failed to load logs.");
      setLines([]);
    } finally {
      setLoading(false);
    }
  }, [level, take]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!autoRefresh) return;
    const id = window.setInterval(() => void load(), 4000);
    return () => window.clearInterval(id);
  }, [autoRefresh, load]);

  return (
    <section className="card controls">
      <h2>Server logs</h2>
      <p className="muted">
        Recent API log lines (newest first). Errors and warnings are highlighted. Data is buffered in memory on the
        server and also written to disk under <code>logs/sitemirror-*.log</code>.
      </p>
      <div className="row log-toolbar">
        <label className="log-field">
          Level
          <select value={level} onChange={(e) => setLevel(e.target.value)}>
            {levels.map((l) => (
              <option key={l || "all"} value={l}>
                {l || "All"}
              </option>
            ))}
          </select>
        </label>
        <label className="log-field">
          Lines
          <input
            type="number"
            min={50}
            max={5000}
            step={50}
            value={take}
            onChange={(e) => setTake(Number(e.target.value) || 500)}
          />
        </label>
        <label className="log-field checkbox">
          <input type="checkbox" checked={autoRefresh} onChange={(e) => setAutoRefresh(e.target.checked)} />
          Auto-refresh (4s)
        </label>
        <button type="button" onClick={() => void load()} disabled={loading}>
          {loading ? "Loading…" : "Refresh now"}
        </button>
      </div>
      {error && <p className="error">{error}</p>}

      {lines.length === 0 && !loading ? (
        <p className="muted">No log lines yet (or filter excluded everything).</p>
      ) : (
        <div className="log-table-wrap">
          <table className="log-table">
            <thead>
              <tr>
                <th>Time (UTC)</th>
                <th>Level</th>
                <th>Message</th>
                <th>Details</th>
              </tr>
            </thead>
            <tbody>
              {lines.map((line, index) => (
                <tr
                  key={`${line.timestampIso}-${index}`}
                  className={
                    line.level?.toLowerCase() === "error" || line.level?.toLowerCase() === "fatal"
                      ? "log-row-error"
                      : line.level?.toLowerCase() === "warning"
                        ? "log-row-warn"
                        : ""
                  }
                >
                  <td className="log-cell-time">
                    <code>{line.timestampIso?.replace("T", " ").replace("Z", "") ?? "-"}</code>
                  </td>
                  <td>
                    <span className={levelClass(line.level)}>{line.level ?? "-"}</span>
                  </td>
                  <td className="log-cell-msg">
                    <pre>{line.message ?? ""}</pre>
                  </td>
                  <td className="log-cell-details">
                    {line.exception ? (
                      <pre className="log-exception">{line.exception}</pre>
                    ) : line.properties && Object.keys(line.properties).length > 0 ? (
                      <pre className="log-props">{JSON.stringify(line.properties, null, 2)}</pre>
                    ) : (
                      <span className="muted">—</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
