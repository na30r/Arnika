# Web Mirror Platform

Production-ready baseline for crawling and mirroring websites into a local Next.js project using an ASP.NET Core backend and SQL Server.

## Repository layout

```text
.
├── backend/
│   └── WebMirror.Api/         # ASP.NET Core API + crawl worker
├── database/
│   ├── migrations/            # versioned SQL migrations
│   └── schema.sql             # bootstrap script (delegates to migrations)
└── frontend/
    ├── app/                   # Next.js app router pages
    ├── lib/                   # mirror file loader
    ├── mirror-data/pages/     # rendered page HTML output
    └── public/mirror/         # downloaded asset output
```

## What is implemented

- `POST /crawl` accepts a URL and enqueues crawl work.
- Background worker processes crawl queue with retries and status tracking.
- Playwright-based rendered page crawl (JS execution + network idle wait).
- Asset downloading with URL-based deduplication and local path rewriting.
- Internal-link rewriting to local mirror routes:
  - `https://example.com/docs/page1` -> `/mirror/example.com/docs/page1`
- SQL metadata persistence:
  - `Pages`, `Assets`, `CrawlQueue`
- Recursive same-domain crawl with depth limit.
- Next.js catch-all route serves mirrored HTML from local storage.

## Backend setup

1. Install .NET SDK 8+
2. Install SQL Server (or run with Docker)
3. From `backend/WebMirror.Api`:

   ```bash
   dotnet restore
   dotnet build
   dotnet run
   ```

   Swagger UI:
   - `http://localhost:5000/swagger`
   - (or your configured ASP.NET Core port)

4. Database migrations:
   - On app startup, backend auto-creates DB (if needed) and applies pending SQL migrations from:
     - `database/migrations/*.sql`
   - Migration history is stored in `dbo.SchemaMigrations`.
   - Configure `ConnectionStrings__MirrorDb` in environment variables or `appsettings`.
   - Optional manual bootstrap: execute `database/schema.sql`.
5. Optional manual Playwright executable override:
   - If Chromium was downloaded manually to a custom location, set:
     - `Mirror__PlaywrightExecutablePath`
   - Example (PowerShell):
     - `$env:Mirror__PlaywrightExecutablePath = "C:\\tools\\playwright\\chrome\\chrome.exe"`
   - Or set `Mirror.PlaywrightExecutablePath` in `appsettings.json`.
6. Install Playwright browser runtime (required once per machine):

   ```bash
   cd backend/WebMirror.Api
   pwsh bin/Debug/net8.0/playwright.ps1 install --with-deps chromium
   ```

## Frontend setup

From `frontend`:

```bash
npm install
npm run dev
```

Mirrored pages are served via:

- `/mirror/{domain}/{path...}`
- Example: `/mirror/example.com/docs/page1`

## API examples

Queue a crawl:

```bash
curl -X POST http://localhost:5000/crawl \
  -H "Content-Type: application/json" \
  -d '{"url":"https://example.com/docs","maxDepth":2}'
```

Check status:

```bash
curl http://localhost:5000/crawl/{queueId}
```

## Notes

- The crawler stores generated HTML under:
  - `frontend/mirror-data/pages/{domain}/.../index.html`
  - legacy location compatibility is kept for old data under `frontend/mirror-data/pages/mirror/{domain}/.../index.html`
- Downloaded assets are stored under:
  - `frontend/public/mirror/{domain}/assets/...`
- Domain whitelist, rate limiting, max depth, and retry behavior are configurable in `appsettings.json`.
- For a first test crawl, try a stable docs page (example: `https://nextjs.org/docs`) and then open the generated local route under `/mirror/nextjs.org/docs`.
