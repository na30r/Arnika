import { notFound } from "next/navigation";
import { locales, type Locale } from "../../../../lib/i18n";

type Props = {
  params: Promise<{ locale: string; path?: string[] }>;
};

/**
 * Wraps static files under /public/mirror in the app layout (navbar, etc.).
 * Visiting /mirror/... is redirected by middleware to /[locale]/mirror/...
 */
export default async function MirrorViewerPage({ params }: Props) {
  const { locale: raw, path } = await params;
  if (!locales.includes(raw as Locale)) {
    notFound();
  }
  const locale = (raw as Locale) || defaultLocale;

  if (!path || path.length === 0) {
    return (
      <main className="page narrow">
        <section className="card controls">
          <h1>Mirrored content</h1>
          <p className="muted">No file path in the URL. Start a mirror from the home page or open a path like /{locale}/mirror/nextjs.org/16.2.5/_localized/en/docs.html</p>
        </section>
      </main>
    );
  }

  const inner = path.map(encodeURIComponent).join("/");
  const src = `/mirror/${inner}`;

  return (
    <main className="page mirror-app-viewer">
      <section className="card viewer mirror-embed-card">
        <div className="viewer-header">
          <h2>Mirrored page</h2>
          <a href={src} target="_blank" rel="noreferrer">
            Open raw file in new tab
          </a>
        </div>
        <iframe className="mirror-embed-frame" src={src} title="Mirrored documentation" />
      </section>
    </main>
  );
}
