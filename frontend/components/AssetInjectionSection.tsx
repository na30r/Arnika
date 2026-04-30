"use client";

import { FormEvent, useState } from "react";
import { authHeaders } from "../lib/auth";

type InjectionAsset = {
  assetId: string;
  assetType: "css" | "js";
  name: string;
  description: string;
  relativeFilePath: string;
  targetPages: string[];
};

export function AssetInjectionSection() {
  const [siteHost, setSiteHost] = useState("nextjs.org");
  const [version, setVersion] = useState("16.2.4");
  const [assetType, setAssetType] = useState<"css" | "js">("css");
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [content, setContent] = useState("");
  const [targetPagesText, setTargetPagesText] = useState("docs");
  const [applyToAllPages, setApplyToAllPages] = useState(false);
  const [assets, setAssets] = useState<InjectionAsset[]>([]);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  function parseTargetPages(): string[] {
    return targetPagesText
      .split("\n")
      .map((line) => line.trim())
      .filter(Boolean);
  }

  async function loadAssets() {
    setLoading(true);
    setError(null);
    try {
      const query = new URLSearchParams({ siteHost: siteHost.trim(), version: version.trim() });
      const response = await fetch(`/api/mirror/injections?${query.toString()}`, {
        headers: { ...authHeaders() },
        cache: "no-store"
      });
      const text = await response.text();
      const payload = text ? (JSON.parse(text) as InjectionAsset[] | { message?: string }) : [];
      if (!response.ok) {
        const msg = !Array.isArray(payload) ? payload.message : "Failed to load assets.";
        throw new Error(msg || `Request failed (${response.status}).`);
      }
      setAssets(Array.isArray(payload) ? payload : []);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Failed to load assets.");
      setAssets([]);
    } finally {
      setLoading(false);
    }
  }

  async function createAsset(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaving(true);
    setError(null);
    setMessage(null);
    try {
      const response = await fetch("/api/mirror/injections", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          ...authHeaders()
        },
        body: JSON.stringify({
          siteHost: siteHost.trim(),
          version: version.trim(),
          assetType,
          name: name.trim(),
          description: description.trim(),
          content,
          targetPages: parseTargetPages(),
          applyToAllPages
        })
      });
      const text = await response.text();
      const payload = text ? (JSON.parse(text) as { message?: string; pagesInjected?: number }) : {};
      if (!response.ok) {
        throw new Error(payload.message || `Create failed (${response.status}).`);
      }
      setMessage(`Asset created and injected into ${payload.pagesInjected ?? 0} page(s).`);
      setName("");
      setDescription("");
      await loadAssets();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Failed to create asset.");
    } finally {
      setSaving(false);
    }
  }

  async function deleteAsset(assetId: string) {
    setError(null);
    setMessage(null);
    try {
      const response = await fetch(`/api/mirror/injections/${encodeURIComponent(assetId)}`, {
        method: "DELETE",
        headers: { ...authHeaders() }
      });
      if (!response.ok && response.status !== 204) {
        const text = await response.text();
        const payload = text ? (JSON.parse(text) as { message?: string }) : {};
        throw new Error(payload.message || `Delete failed (${response.status}).`);
      }
      setMessage("Asset deleted.");
      await loadAssets();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Failed to delete asset.");
    }
  }

  return (
    <section className="card controls">
      <h2>Asset Injection</h2>
      <p className="muted">Create CSS/JS and inject into mirrored pages.</p>
      <form className="form" onSubmit={createAsset}>
        <label htmlFor="inj-site">Site Host</label>
        <input id="inj-site" value={siteHost} onChange={(e) => setSiteHost(e.target.value)} />
        <label htmlFor="inj-version">Version</label>
        <input id="inj-version" value={version} onChange={(e) => setVersion(e.target.value)} />
        <label htmlFor="inj-type">Asset Type</label>
        <select id="inj-type" value={assetType} onChange={(e) => setAssetType(e.target.value as "css" | "js")}>
          <option value="css">css</option>
          <option value="js">js</option>
        </select>
        <label htmlFor="inj-name">Name</label>
        <input id="inj-name" value={name} onChange={(e) => setName(e.target.value)} required />
        <label htmlFor="inj-description">Description</label>
        <input id="inj-description" value={description} onChange={(e) => setDescription(e.target.value)} />
        <label htmlFor="inj-content">Content</label>
        <textarea id="inj-content" rows={8} value={content} onChange={(e) => setContent(e.target.value)} required />
        <label htmlFor="inj-targets">Target Pages (one per line)</label>
        <textarea
          id="inj-targets"
          rows={4}
          value={targetPagesText}
          onChange={(e) => setTargetPagesText(e.target.value)}
          disabled={applyToAllPages}
        />
        <label>
          <input type="checkbox" checked={applyToAllPages} onChange={(e) => setApplyToAllPages(e.target.checked)} />{" "}
          Apply to all pages
        </label>
        <div className="row">
          <button type="submit" disabled={saving}>{saving ? "Saving..." : "Create Injection"}</button>
          <button type="button" onClick={loadAssets} disabled={loading}>{loading ? "Loading..." : "Refresh Assets"}</button>
        </div>
      </form>
      {error && <p className="error">{error}</p>}
      {message && <p className="muted">{message}</p>}

      {assets.length > 0 && (
        <div className="queue-list">
          {assets.map((asset) => (
            <div className="card" key={asset.assetId} style={{ marginBottom: 12 }}>
              <div className="meta">
                <div><strong>{asset.name}</strong> ({asset.assetType})</div>
                <div><strong>ID:</strong> <code>{asset.assetId}</code></div>
                <div><strong>Pages:</strong> {asset.targetPages?.length ?? 0}</div>
                <div><strong>Path:</strong> <code>{asset.relativeFilePath}</code></div>
              </div>
              <div className="row">
                <button type="button" onClick={() => deleteAsset(asset.assetId)}>Delete</button>
              </div>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}
