import { readFile } from "fs/promises";
import { extname, join, resolve, sep } from "path";
import { NextRequest, NextResponse } from "next/server";
import { validCookiePair, verifyToken, jwtToUserPayload } from "@/lib/auth-session";

const mirrorRoot = resolve(process.cwd(), "public", "mirror");

const mime: Record<string, string> = {
  ".html": "text/html; charset=utf-8",
  ".htm": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".map": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".jpeg": "image/jpeg",
  ".gif": "image/gif",
  ".webp": "image/webp",
  ".ico": "image/x-icon",
  ".woff": "font/woff",
  ".woff2": "font/woff2",
  ".ttf": "font/ttf"
};

type Params = { path?: string[] };

function safeAbsoluteFile(pathSegments: string[] | undefined): string | null {
  if (!pathSegments?.length) {
    return null;
  }
  for (const p of pathSegments) {
    if (p.includes("\0") || p === ".." || p === ".") {
      return null;
    }
  }
  const abs = resolve(mirrorRoot, join(...pathSegments));
  if (abs !== mirrorRoot && !abs.startsWith(mirrorRoot + sep)) {
    return null;
  }
  if (abs === mirrorRoot) {
    return null;
  }
  return abs;
}

function contentTypeFor(filePath: string) {
  const ext = extname(filePath).toLowerCase();
  return mime[ext] ?? "application/octet-stream";
}

async function requireSession(request: NextRequest) {
  const token = await validCookiePair(request);
  if (!token) {
    return { ok: false as const, response: NextResponse.json({ message: "Sign in required." }, { status: 401 }) };
  }
  try {
    const payload = await verifyToken(token);
    const user = jwtToUserPayload(payload);
    if (!user) {
      return { ok: false as const, response: NextResponse.json({ message: "Sign in required." }, { status: 401 }) };
    }
  } catch {
    return { ok: false as const, response: NextResponse.json({ message: "Sign in required." }, { status: 401 }) };
  }
  return { ok: true as const };
}

export async function GET(request: NextRequest, context: { params: Promise<Params> }) {
  const { path: segs } = await context.params;
  const abs = safeAbsoluteFile(segs);
  if (abs == null) {
    return new NextResponse("Not found", { status: 404 });
  }

  const check = await requireSession(request);
  if (!check.ok) {
    return check.response;
  }

  let buf: Buffer;
  try {
    buf = await readFile(abs);
  } catch {
    return new NextResponse("Not found", { status: 404 });
  }
  return new NextResponse(new Uint8Array(buf), {
    status: 200,
    headers: { "content-type": contentTypeFor(abs), "cache-control": "private, max-age=60" }
  });
}
