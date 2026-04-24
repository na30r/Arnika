import Link from "next/link";
import { notFound } from "next/navigation";
import { loadMirrorPageHtml } from "../../../../lib/loadMirrorPage";
import { locales, type Locale } from "../../../../lib/i18n";

type Props = {
  params: Promise<{ locale: string; path?: string[] }>;
};

/**
 * Renders mirrored HTML inside the app shell (navbar) without an iframe: the file is
 * read from public/mirror, asset URLs are rewritten to /mirror/..., and the body is injected.
 * Note: inline & external scripts in the fragment do not re-execute; use "Open full page" for
 * a fully dynamic mirror.
 */
export default async function MirrorViewerPage({ params }: Props) {
  const { locale: raw, path } = await params;
  if (!locales.includes(raw as Locale)) {
    notFound();
  }
  const locale = raw as Locale;

  if (!path || path.length === 0) {
    return (
      <main className="page narrow">
        <section className="card controls">
          <h1>Mirrored content</h1>
          <p className="muted">
            Add a path after <code>/{locale}/mirror/</code>, e.g.{" "}
            <code>nextjs.org/16.2.5/_localized/en/docs.html</code>
          </p>
        </section>
      </main>
    );
  }

  const result = await loadMirrorPageHtml(path);

  if ("error" in result) {
    if (result.status === 404) {
      notFound();
    }
    return (
      <main className="page narrow">
        <section className="card controls">
          <h1>Cannot show this file</h1>
          <p className="error">{result.error}</p>
          <p className="muted">
            Raw URL:{" "}
            <a href={"/mirror/" + path.map(encodeURIComponent).join("/")} target="_blank" rel="noreferrer">
              /mirror/…
            </a>
          </p>
          <p>
            <Link href={`/${locale}/`}>Home</Link>
          </p>
        </section>
      </main>
    );
  }

  return (
    <main className="page mirror-app-viewer">
      <div className="mirror-shell-toolbar card">
        <h2 className="mirror-shell-title">{result.title}</h2>
        <a className="btn-ghost" href={result.rawPath} target="_blank" rel="noreferrer">
          Open full page in new tab
        </a>
        <p className="muted small-note mirror-shell-note">
          In-app view loads styles and most assets; some mirrors rely on full-page JavaScript. Use the link
          above if the page does not work fully here.
        </p>
      </div>
      <div className="mirror-shtml-root" suppressHydrationWarning>
        {result.headHtml ? (
          <div className="mirror-shtml-head" suppressHydrationWarning dangerouslySetInnerHTML={{ __html: result.headHtml }} />
        ) : null}
        <div className="mirror-shtml-body" suppressHydrationWarning dangerouslySetInnerHTML={{ __html: result.bodyHtml }} />
      </div>
    </main>
  );
}
