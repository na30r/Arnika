import { promises as fs } from 'fs';
import path from 'path';

const pagesRoot = path.join(process.cwd(), 'mirror-data', 'pages');

export function normalizeMirrorSlug(slug: string[]) {
  return slug.map((segment) => segment.trim()).filter(Boolean);
}

export function resolveMirrorHtmlPath(slug: string[]) {
  const clean = normalizeMirrorSlug(slug);
  return path.join(pagesRoot, ...clean, 'index.html');
}

export async function readMirroredPage(slug: string[]) {
  const primaryPath = resolveMirrorHtmlPath(slug);
  try {
    const html = await fs.readFile(primaryPath, 'utf8');
    return { html, htmlPath: primaryPath };
  } catch {
    // Backward compatibility for pages saved by older backend path mapping.
    const legacyPath = path.join(pagesRoot, 'mirror', ...normalizeMirrorSlug(slug), 'index.html');
    const html = await fs.readFile(legacyPath, 'utf8');
    return { html, htmlPath: legacyPath };
  }
}
