# SiteMirror (.NET)

Small .NET tool to mirror a web page locally:

- Opens a URL in a real Chromium browser (Playwright).
- Waits for the page to fully load (`networkidle`) plus extra wait time.
- Saves rendered HTML (post-JavaScript rendering).
- Captures network responses and linked assets (CSS, JS, images, fonts, iframes, etc.).
- Rewrites links so the mirrored page can be opened from local files/server.

## Requirements

- .NET 8 SDK
- Internet access for first-time Playwright browser download

## Build

```bash
dotnet restore SiteMirror/SiteMirror.csproj
dotnet build SiteMirror/SiteMirror.csproj
```

## Run

```bash
dotnet run --project SiteMirror -- "https://example.com" "./mirror-output" 4000
```

Arguments:

1. `url` (required) - page to mirror
2. `output-folder` (optional, default: `mirror-output`)
3. `extra-wait-ms` (optional, default: `4000`) - extra wait after network idle for late JS rendering

## Preview mirrored page

Start a static file server from output:

```bash
python3 -m http.server 8080 --directory "./mirror-output/<generated-folder>"
```

Then open the generated entry file URL printed by the tool.

## Notes

- Pages requiring login, anti-bot checks, or highly dynamic runtime APIs may still differ.
- This tool mirrors the loaded state of a page, not server-side business logic.
