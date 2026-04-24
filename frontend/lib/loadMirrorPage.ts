import { readFile } from "node:fs/promises";
import { relative, resolve } from "node:path";
import { load } from "cheerio";

const ORIGIN = "https://__mirror.app";

function resolveAttr(value: string, pathSegments: string[], pageFile: string): string {
  const t = value.trim();
  if (!t) {
    return t;
  }
  if (t.startsWith("data:") || t.startsWith("javascript:") || t.startsWith("mailto:") || t.startsWith("blob:")) {
    return t;
  }
  if (t.startsWith("#")) {
    return t;
  }
  if (t.startsWith("//")) {
    return t;
  }
  if (t.startsWith("/")) {
    if (t === "/mirror" || t.startsWith("/mirror/")) {
      return t;
    }
    return "/mirror" + t;
  }

  const dirPath = "/mirror/" + pathSegments.slice(0, -1).map(encodeURIComponent).join("/") + "/";
  const u = new URL(t, ORIGIN + dirPath);
  if (u.origin === ORIGIN) {
    return u.pathname + u.search + u.hash;
  }
  return t;
}

function rewriteSrcset(value: string, pathSegments: string[], pageFile: string): string {
  return value
    .split(",")
    .map((part) => {
      const s = part.trim();
      const i = s.search(/\s/);
      if (i < 0) {
        return resolveAttr(s, pathSegments, pageFile);
      }
      return resolveAttr(s.slice(0, i), pathSegments, pageFile) + s.slice(i);
    })
    .join(", ");
}

export type LoadMirrorResult =
  | { bodyHtml: string; rawPath: string; title: string }
  | { error: string; status: 404 | 400 | 500 };

/**
 * Read mirrored HTML from public/mirror, resolve asset paths to /mirror/..., return body
 * to embed under the app shell (no iframe, no document-wide <base>).
 */
export async function loadMirrorPageHtml(pathSegments: string[]): Promise<LoadMirrorResult> {
  if (pathSegments.length === 0) {
    return { error: "No path", status: 400 };
  }

  for (const seg of pathSegments) {
    if (seg.includes("..") || seg.includes("/") || seg.includes("\\")) {
      return { error: "Invalid path", status: 400 };
    }
  }

  const fileName = pathSegments[pathSegments.length - 1] ?? "";
  if (!/\.(html?|HTML?)$/.test(fileName)) {
    return {
      error:
        "Only .html / .htm pages can be shown in the app shell. Open other files in a new tab from the raw /mirror/... URL.",
      status: 400
    };
  }

  const mirrorRoot = resolve(process.cwd(), "public", "mirror");
  const fileAbs = resolve(mirrorRoot, ...pathSegments);
  const relToRoot = relative(mirrorRoot, fileAbs);
  if (relToRoot.startsWith("..") || relToRoot.startsWith("/")) {
    return { error: "Invalid path", status: 400 };
  }

  let file: string;
  try {
    file = await readFile(fileAbs, "utf8");
  } catch {
    return { error: "File not found. Run a mirror for this version first.", status: 404 };
  }

  const $ = load(file);
  const pageFile = fileName;
  $("base").remove();

  type El = { attribs?: Record<string, string> };

  const setAttr = (el: El, name: "href" | "src" | "action") => {
    const v = el.attribs?.[name];
    if (v) {
      el.attribs ??= {};
      el.attribs[name] = resolveAttr(v, pathSegments, pageFile);
    }
  };

  $("script[src]").each((_, el) => setAttr(el as El, "src"));
  $('link[rel="stylesheet"][href], link[rel="preload"][href], link[rel="modulepreload"][href]').each((_, el) =>
    setAttr(el as El, "href")
  );
  $("img[src], source[src], video[src], audio[src], track[src], embed[src], iframe[src]").each((_, el) =>
    setAttr(el as El, "src")
  );
  $("form[action]").each((_, el) => setAttr(el as El, "action"));
  $("a[href]").each((_, el) => setAttr(el as El, "href"));

  $('img[srcset], source[srcset]').each((_, el) => {
    const a = (el as El).attribs;
    if (a?.srcset) {
      a.srcset = rewriteSrcset(a.srcset, pathSegments, pageFile);
    }
  });

  const title = $("title").first().text() || "Mirrored page";
  const body = $("body").first();
  const bodyHtml = body.length > 0 ? body.html() ?? "" : $.html();
  const rawPath = `/mirror/${pathSegments.map(encodeURIComponent).join("/")}`;
  return { bodyHtml, rawPath, title };
}
