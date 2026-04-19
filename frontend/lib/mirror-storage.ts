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
  const htmlPath = resolveMirrorHtmlPath(slug);
  const html = await fs.readFile(htmlPath, 'utf8');
  return { html, htmlPath };
}
