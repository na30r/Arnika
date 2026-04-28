/**
 * Translate fa.template.json -> fa.json (Persian), preserving English product/API names.
 * Usage: node translate-fa-template.mjs <path-to-fa.template.json> [out-path]
 */
import { readFileSync, writeFileSync } from "fs";
import { dirname, join } from "path";
import { fileURLToPath } from "url";

const __dirname = fileURLToPath(new URL(".", import.meta.url));

// Longest first so "App Router" before "Router", "Next.js" before "Next"
const PROTECT = [
  "App Router and Pages Router",
  "The App Router and Pages Router",
  "Next.js App Router",
  "Pages Router",
  "App Router",
  "useRouter",
  "usePathname",
  "useParams",
  "useSearchParams",
  "useSelectedLayoutSegments",
  "useSelectedLayoutSegment",
  "useReportWebVitals",
  "useLightningcss",
  "use cache",
  "use cache: remote",
  "NextRequest",
  "NextResponse",
  "Next.js",
  "create-next-app",
  "next.config.js",
  "next CLI",
  "Vercel",
  "React",
  "Router",
  "Turbopack",
  "turbopack",
  "TypeScript",
  "JavaScript",
  "ESLint",
  "PostCSS",
  "Tailwind CSS",
  "OpenTelemetry",
  "Playwright",
  "Cypress",
  "Jest",
  "Vitest",
  "Babel",
  "Webpack",
  "webpack",
  "Rspack",
  "PWA",
  "PWAs",
  "SSG",
  "ISR",
  "CLI",
  "PPR",
  "RSC",
  "SPA",
  "SPAs",
  "MDX",
  "SSR",
  "CSR",
  "CSP",
  "JSON-LD",
  "GitHub",
  "Bluesky",
  "Twitter",
  "X",
  "Chrome",
  "Vite",
  "Sass",
  "npm",
  "npx",
  "Node.js",
  "Node",
  "Next",
  "React Compiler",
  "ImageResponse",
  "Not Found",
  "getStaticProps",
  "getServerSideProps",
  "getInitialProps",
  "getStaticPaths"
].sort((a, b) => b.length - a.length);

function shouldSkipTranslate(s) {
  if (!s || typeof s !== "string") return true;
  const t = s.trim();
  if (t.length < 2) return true;
  // RSC / flight / code blobs
  if (t.length > 800) return true;
  if (/\$react\.|__next_f|children\":/.test(t)) return true;
  if (/^[\d\s,.\-_:;{}\[\]()'"\\/]+$/.test(t)) return true;
  if (/^https?:\/\//i.test(t)) return true;
  if (/^[\w.-]+\.(js|jsx|ts|tsx|json|css|md|mdx)$/i.test(t)) return true;
  if (/^[/\\]/.test(t) && t.length < 200) {
    if (!/[a-zA-Z]{3,}/.test(t)) return true;
  }
  return false;
}

function protectTerms(text) {
  const placeholders = [];
  let s = text;
  let i = 0;
  for (const term of PROTECT) {
    if (!term) continue;
    const re = new RegExp(term.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "g");
    s = s.replace(re, () => {
      const id = `{{__T${i++}__}}`;
      placeholders.push({ id, term });
      return id;
    });
  }
  return { masked: s, placeholders };
}

function unprotectTerms(text, placeholders) {
  let s = text;
  for (const { id, term } of placeholders) {
    s = s.split(id).join(term);
  }
  return s;
}

async function gtranslateEnToFa(text) {
  const url =
    "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=fa&dt=t&q=" +
    encodeURIComponent(text);
  const res = await fetch(url, { headers: { "User-Agent": "SiteMirror-Translate/1.0" } });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  const data = await res.json();
  return data[0].map((x) => x[0]).join("");
}

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

const inputPath = process.argv[2];
const outputPath = process.argv[3];

if (!inputPath) {
  console.error("Usage: node translate-fa-template.mjs <fa.template.json> [out fa.json]");
  process.exit(1);
}

let raw = readFileSync(inputPath, "utf8");
if (raw.charCodeAt(0) === 0xfeff) {
  raw = raw.slice(1);
}
const doc = JSON.parse(raw);
if (!doc.Entries) {
  console.error("Invalid file: expected Entries");
  process.exit(1);
}

const out = { Language: "fa", Entries: {} };
const keys = Object.keys(doc.Entries).sort();
let done = 0;
const delayMs = 80;

for (const k of keys) {
  const val = doc.Entries[k];
  if (shouldSkipTranslate(val)) {
    out.Entries[k] = val;
  } else {
    const { masked, placeholders } = protectTerms(String(val));
    let translated;
    try {
      translated = await gtranslateEnToFa(masked);
    } catch (e) {
      console.warn("Translate failed for key", k, e.message);
      translated = val;
    }
    out.Entries[k] = unprotectTerms(translated, placeholders);
    await sleep(delayMs);
  }
  done++;
  if (done % 50 === 0) {
    process.stderr.write(`Progress ${done}/${keys.length}\n`);
  }
}

const target =
  outputPath || join(dirname(inputPath), "fa.json");
writeFileSync(target, JSON.stringify(out, null, 2) + "\n", "utf8");
console.log("Wrote", target, "keys:", keys.length);
