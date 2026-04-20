# SiteMirror API (.NET + Swagger)

ASP.NET Core Web API to mirror a web page locally:

- Uses Playwright Chromium to render the page (including JavaScript).
- Waits for complete load (`networkidle`) plus configurable extra wait.
- Saves rendered HTML + downloaded resources (CSS, JS, images, fonts, iframes, etc.).
- Rewrites links for local offline preview.
- Exposes API endpoints with Swagger UI.

## Requirements

- .NET 8 SDK
- Chromium executable path (optional manual path), or internet access for first-time Playwright browser download

## Build

```bash
dotnet restore SiteMirror.sln
dotnet build SiteMirror.sln
```

## Run API

```bash
dotnet run --project SiteMirror.Api
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
  "outputFolder": "mirror-output",
  "extraWaitMs": 4000,
  "chromiumExecutablePath": "/usr/bin/chromium-browser"
}
```

Fields:

1. `url` (required) - page to mirror
2. `outputFolder` (optional, default: `mirror-output`)
3. `extraWaitMs` (optional, default: `4000`)
4. `chromiumExecutablePath` (optional) - full path to Chromium/Chrome executable

If `chromiumExecutablePath` is provided, the API launches that browser directly and skips Playwright browser install.

## Notes

- Pages requiring login, anti-bot checks, or dynamic backend APIs may still differ.
- This mirrors rendered frontend state, not server-side business actions.
