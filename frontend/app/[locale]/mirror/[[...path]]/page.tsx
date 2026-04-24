import Link from "next/link";
import { notFound } from "next/navigation";
import { MirrorHeadInjector } from "../../../../components/MirrorHeadInjector";
import { loadMirrorPageHtml } from "../../../../lib/loadMirrorPage";
import { locales, type Locale } from "../../../../lib/i18n";

type Props = {
  params: Promise<{ locale: string; path?: string[] }>;
};

/**
 * Mirrored HTML under app shell: styles/scripts in <head> are injected into document.head
 * via MirrorHeadInjector so stylesheets load; body is rendered below the toolbar.
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
      {result.headHtml ? <MirrorHeadInjector headHtml={result.headHtml} /> : null}
      <div className="mirror-shell-toolbar card">
        <h2 className="mirror-shell-title">{result.title}</h2>
        <a className="btn-ghost" href={result.rawPath} target="_blank" rel="noreferrer">
          Open full page in new tab
        </a>
        <p className="muted small-note mirror-shell-note">
          Styles are loaded from the mirroring folder. If layout still looks wrong, the site may need its
          JavaScript; use the link above.
        </p>
      </div>
      <div className="mirror-shtml-root" suppressHydrationWarning dangerouslySetInnerHTML={{ __html: result.bodyHtml }} />
    </main>
  );
}
