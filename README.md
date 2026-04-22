# SiteMirror API (.NET + Swagger)

ASP.NET Core Web API to mirror a web page locally:

- Uses Playwright Chromium to render the page (including JavaScript).
- Waits for complete load (`networkidle`) plus configurable extra wait.
- Saves rendered HTML + downloaded resources (CSS, JS, images, fonts, iframes, etc.).
- Rewrites links for local offline preview.
- Exposes API endpoints with Swagger UI.

## Requirements

- .NET 8 SDK
- Configure mirror paths in `appsettings.json` / `appsettings.Development.json`

## Build

```bash
dotnet restore SiteMirror.sln
dotnet build SiteMirror.sln
```

## Run API

```bash
dotnet run --project SiteMirror.Api
```

Mirror configuration now comes from app settings:

```json
"MirrorSettings": {
  "OutputFolder": "mirror-output",
  "ChromiumExecutablePath": "/usr/local/bin/google-chrome"
}
```

Open Swagger UI:

- `http://localhost:5107/swagger` (HTTP, default launch profile)
- `https://localhost:7213/swagger` (HTTPS, default launch profile)

## Mirror endpoint

`POST /api/mirror`

Request body:

```json
{
  "url": "https://example.com",
  "extraWaitMs": 4000
}
```

Fields:

1. `url` (required) - page to mirror
2. `extraWaitMs` (optional, default: `4000`)

Path and folder are read from app settings (`MirrorSettings`) instead of request payload.

## Notes

- Pages requiring login, anti-bot checks, or dynamic backend APIs may still differ.
- This mirrors rendered frontend state, not server-side business actions.
