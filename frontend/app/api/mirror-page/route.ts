import { NextRequest, NextResponse } from "next/server";
import { promises as fs } from "node:fs";
import path from "node:path";

export const runtime = "nodejs";

const runtimeSnippet = '<script data-site-mirror-runtime="1" src="/mirror/_mirror-runtime.js"></script>';

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

export async function GET(request: NextRequest) {
  const rawPath = request.nextUrl.searchParams.get("path");
  const requestedPath = (() => {
    if (!rawPath) {
      return "";
    }

    let normalized = rawPath.trim();
    try {
      normalized = decodeURIComponent(normalized);
    } catch {
      // Keep raw value when decode fails.
    }

    const queryIndex = normalized.indexOf("?");
    const hashIndex = normalized.indexOf("#");
    let endIndex = normalized.length;
    if (queryIndex >= 0) {
      endIndex = Math.min(endIndex, queryIndex);
    }
    if (hashIndex >= 0) {
      endIndex = Math.min(endIndex, hashIndex);
    }
    normalized = normalized.slice(0, endIndex);
    if (normalized.endsWith("/")) {
      normalized = normalized.slice(0, -1);
    }

    return normalized;
  })();

  if (!requestedPath || !requestedPath.startsWith("/mirror/") || !requestedPath.toLowerCase().endsWith(".html")) {
    return NextResponse.json({ message: "Invalid mirror page path." }, { status: 400 });
  }

  const relativePath = requestedPath.slice(1);
  const filePath = path.join(process.cwd(), "public", relativePath);
  const normalizedRoot = path.resolve(process.cwd(), "public");
  const normalizedFile = path.resolve(filePath);
  if (!normalizedFile.startsWith(`${normalizedRoot}${path.sep}`)) {
    return NextResponse.json({ message: "Invalid path." }, { status: 400 });
  }

  let html: string;
  try {
    html = await fs.readFile(normalizedFile, "utf8");
  } catch {
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
