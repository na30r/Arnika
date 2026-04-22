# SiteMirror (.NET API + Next.js frontend)

Mirror documentation pages (including JavaScript-rendered Next.js/Tailwind sites) and serve them from one local Next.js application.

## What this project does

- Crawls a **single page** with Playwright (rendered DOM, scripts, styles, fonts, images, iframes).
- Captures response assets and downloads linked resources that were not directly observed.
- Rewrites links to local relative paths for offline/local serving.
- Saves mirrored output into `frontend/public/mirror` so Next.js serves it at `/mirror/...`.
- Pre-generates localized HTML copies and translation catalogs per mirrored site/version.
- Provides a Next.js UI that:
  - triggers crawl requests,
  - returns preview metadata,
  - previews the mirrored page in an iframe.

## Repository layout

```text
.
├── SiteMirror.Api/          # ASP.NET Core mirror API
├── frontend/                # Next.js frontend + preview UI
└── SiteMirror.sln
```

## Requirements

- .NET 8 SDK
- Node.js 20+

## Backend setup (ASP.NET Core)

```bash
dotnet restore SiteMirror.sln
dotnet build SiteMirror.sln
dotnet run --project SiteMirror.Api
```

Default API URL (launch profile):

- `http://localhost:5196`
- Swagger: `http://localhost:5196/swagger`

Mirror settings in `SiteMirror.Api/appsettings*.json`:

```json
"MirrorSettings": {
  "OutputFolder": "../frontend/public/mirror",
  "ChromiumExecutablePath": null
}
```

Notes:

- If `ChromiumExecutablePath` is `null`, Playwright installs/uses bundled Chromium.
- In development, HTTPS redirection is disabled to simplify local Next -> API calls.

## Frontend setup (Next.js)

```bash
cd frontend
npm install
npm run dev
```

Default frontend URL:

- `http://localhost:3000`

Optional environment variable:

```bash
cp .env.example .env.local
```

`.env.example`:

```bash
MIRROR_API_BASE_URL=http://localhost:5196
```

## Crawl and preview flow

1. Open `http://localhost:3000`.
2. Enter a documentation page URL (for example: `https://nextjs.org/docs`).
3. Click **Mirror page**.
4. The UI calls `POST /api/mirror` (Next route handler), which proxies to the ASP.NET API.
5. The mirrored output is saved under `frontend/public/mirror/<host>/<version>/...`.
6. Localized copies are generated under:
   - `frontend/public/mirror/<host>/<version>/_localized/<lang>/...`
   - translation catalogs under `frontend/public/mirror/<host>/<version>/_i18n/`
7. Preview is served by Next at `/mirror/<host>/<version>/_localized/<lang>/...`.

## API: `POST /api/mirror`

Example request:

```json
{
  "url": "https://nextjs.org/docs",
  "version": "v14.2.0",
  "languages": ["en", "fa"],
  "extraWaitMs": 4000,
  "autoScroll": true,
  "scrollStepPx": 1200,
  "scrollDelayMs": 150,
  "maxScrollRounds": 24
}
```

Fields:

1. `url` (required): target page URL. If scheme is missing, API uses `https://`.
2. `version` (optional): mirror version folder name (default: `latest`).
3. `languages` (optional): list of language codes to pre-generate. `en` is always generated and used as default.
4. `extraWaitMs` (optional): additional wait after page load.
5. `autoScroll` (optional): scrolls page to trigger lazy-loaded docs content/resources.
6. `scrollStepPx` (optional): pixels scrolled each step.
7. `scrollDelayMs` (optional): wait between scroll steps.
8. `maxScrollRounds` (optional): max scroll iterations.

Example response fields:

- `siteHost`: sanitized target host folder name (for example `nextjs.org`).
- `version`: effective mirror version used for storage/routing.
- `defaultLanguage`: default generated language (`en`).
- `availableLanguages`: generated language list.
- `frontendPreviewPath`: local route for iframe or browser (`/mirror/<site>/<version>/_localized/<lang>/...`).
- `entryFileRelativePath`: mirrored entry file path under mirror root.
- `filesSaved`: number of mapped files saved.

## Current scope

- Optimized for **single-page** documentation mirroring.
- Authentication/cookies are not required for the main flow.
- Dynamic backend data and gated pages can still differ from source.
