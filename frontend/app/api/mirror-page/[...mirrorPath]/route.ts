import { NextRequest, NextResponse } from "next/server";
import { promises as fs } from "node:fs";
import path from "node:path";

export const runtime = "nodejs";

const runtimeSnippet = '<script data-site-mirror-runtime="1" src="/mirror/_mirror-runtime.js"></script>';

function resolveMirrorHtmlPath(requestedPath: string): string[] {
  const trimmed = requestedPath.trim().replace(/\/+$/g, "");
  if (!trimmed) {
    return [];
  }

  const candidates = new Set<string>();
  candidates.add(trimmed);

  if (trimmed.toLowerCase().endsWith(".html")) {
    const withoutHtml = trimmed.slice(0, -".html".length);
    candidates.add(`${withoutHtml}/index.html`);
  } else {
    candidates.add(`${trimmed}.html`);
    candidates.add(`${trimmed}/index.html`);
  }

  return Array.from(candidates);
}

function injectRuntimeScript(html: string): string {
  if (html.includes('data-site-mirror-runtime="1"')) {
    return html;
  }

  const headOpenMatch = html.match(/<head[^>]*>/i);
  if (headOpenMatch?.index !== undefined) {
    const insertAt = headOpenMatch.index + headOpenMatch[0].length;
    return `${html.slice(0, insertAt)}${runtimeSnippet}${html.slice(insertAt)}`;
  }

  const htmlOpenMatch = html.match(/<html[^>]*>/i);
  if (htmlOpenMatch?.index !== undefined) {
    const insertAt = htmlOpenMatch.index + htmlOpenMatch[0].length;
    return `${html.slice(0, insertAt)}<head>${runtimeSnippet}</head>${html.slice(insertAt)}`;
  }

  return `${runtimeSnippet}${html}`;
}

export async function GET(
  _request: NextRequest,
  { params }: { params: Promise<{ mirrorPath: string[] }> }
) {
  const { mirrorPath } = await params;
  if (!Array.isArray(mirrorPath) || mirrorPath.length < 2 || mirrorPath[0] !== "mirror") {
    return NextResponse.json({ message: "Invalid mirror page path." }, { status: 400 });
  }

  const requestedPath = `/${mirrorPath.join("/")}`;
  if (!requestedPath.toLowerCase().endsWith(".html")) {
    return NextResponse.json({ message: "Invalid mirror page path." }, { status: 400 });
  }

  const normalizedRoot = path.resolve(process.cwd(), "public");
  let html: string | null = null;
  for (const candidate of resolveMirrorHtmlPath(requestedPath)) {
    const relativePath = candidate.slice(1);
    const filePath = path.join(process.cwd(), "public", relativePath);
    const normalizedFile = path.resolve(filePath);
    if (!normalizedFile.startsWith(`${normalizedRoot}${path.sep}`)) {
      return NextResponse.json({ message: "Invalid path." }, { status: 400 });
    }

    try {
      html = await fs.readFile(normalizedFile, "utf8");
      break;
    } catch {
      // Try next candidate path form.
    }
  }

  if (html === null) {
    return NextResponse.json({ message: "Mirror page not found." }, { status: 404 });
  }

  const withRuntime = injectRuntimeScript(html);
  return new NextResponse(withRuntime, {
    status: 200,
    headers: {
      "Content-Type": "text/html; charset=utf-8",
      "Cache-Control": "no-store"
    }
  });
}
