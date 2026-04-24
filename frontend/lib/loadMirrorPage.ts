import { readFile } from "node:fs/promises";
import { relative, resolve } from "node:path";
import { load } from "cheerio";

const ORIGIN = "https://__mirror.app";

/** e.g. nextjs.org/16.2.5/... -> prefix "nextjs.org%2F16.2.5" for /_next/... -> /mirror/host/ver/_next/... */
function getSitePrefixPath(pathSegments: string[]): string {
  if (pathSegments.length >= 2) {
    return [pathSegments[0]!, pathSegments[1]!].map(encodeURIComponent).join("/");
  }
  if (pathSegments.length === 1) {
    return encodeURIComponent(pathSegments[0]!);
  }
  return "";
}

/**
 * Map root-absolute paths from the mirrored page (/_next/..., /_localized/...) to
 * /mirror/{host}/{version}/... under public/mirror.
 */
function resolveAttr(value: string, pathSegments: string[]): string {
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
    const sitePrefix = getSitePrefixPath(pathSegments);
    if (!sitePrefix) {
      return t;
    }
    const hashIndex = t.indexOf("#");
    const beforeHash = hashIndex >= 0 ? t.slice(0, hashIndex) : t;
    const hash = hashIndex >= 0 ? t.slice(hashIndex) : "";
    const qIndex = beforeHash.indexOf("?");
    const pathAndQuery = qIndex >= 0 ? beforeHash.slice(0, qIndex) : beforeHash;
    const query = qIndex >= 0 ? beforeHash.slice(qIndex) : "";
    return `/mirror/${sitePrefix}${pathAndQuery}${query}${hash}`;
  }

  const dirPath = "/mirror/" + pathSegments.slice(0, -1).map(encodeURIComponent).join("/") + "/";
  const u = new URL(t, ORIGIN + dirPath);
  if (u.origin === ORIGIN) {
    return u.pathname + u.search + u.hash;
  }
  return t;
}

function rewriteSrcset(value: string, pathSegments: string[]): string {
  return value
    .split(",")
    .map((part) => {
      const s = part.trim();
      const i = s.search(/\s/);
      if (i < 0) {
        return resolveAttr(s, pathSegments);
      }
      return resolveAttr(s.slice(0, i), pathSegments) + s.slice(i);
    })
    .join(", ");
}

function rewriteInlineCssUrls(css: string, pathSegments: string[]): string {
  return css.replace(/url\(\s*(['"]?)(\/[^'")]+)\1\s*\)/gi, (_m, _q, path) => {
    return `url(${_q as string}${resolveAttr(path as string, pathSegments)}${(_q as string) || ""})`;
  });
}

export type LoadMirrorResult =
  | { headHtml: string; bodyHtml: string; rawPath: string; title: string }
  | { error: string; status: 404 | 400 | 500 };

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
  $("base").remove();

  type El = { attribs?: Record<string, string> };

  const setAttr = (el: El, name: "href" | "src" | "action") => {
    const v = el.attribs?.[name];
    if (v) {
      el.attribs ??= {};
      el.attribs[name] = resolveAttr(v, pathSegments);
    }
  };

  $("script[src]").each((_, el) => setAttr(el as El, "src"));
  $("link[href]").each((_, el) => setAttr(el as El, "href"));
  $("img[src], source[src], video[src], audio[src], track[src], embed[src], iframe[src]").each((_, el) =>
    setAttr(el as El, "src")
  );
  $("form[action]").each((_, el) => setAttr(el as El, "action"));
  $("a[href]").each((_, el) => setAttr(el as El, "href"));

  $("style").each((_, el) => {
    const h = $(el).html();
    if (h) {
      $(el).html(rewriteInlineCssUrls(h, pathSegments));
    }
  });

  $("[style]").each((_, el) => {
    const a = (el as El).attribs;
    if (a?.style) {
      a.style = rewriteInlineCssUrls(a.style, pathSegments);
    }
  });

  $('img[srcset], source[srcset]').each((_, el) => {
    const a = (el as El).attribs;
    if (a?.srcset) {
      a.srcset = rewriteSrcset(a.srcset, pathSegments);
    }
  });

  const title = $("title").first().text() || "Mirrored page";
  $("head title").remove();
  $("head script").remove();
  const head = $("head").first();
  const headHtml = head.length > 0 ? head.html() ?? "" : "";
  const body = $("body").first();
  const bodyHtml = body.length > 0 ? body.html() ?? "" : $.html();
  const rawPath = `/mirror/${pathSegments.map(encodeURIComponent).join("/")}`;
  return { headHtml, bodyHtml, rawPath, title };
}
