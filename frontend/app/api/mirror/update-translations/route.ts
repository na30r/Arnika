import { NextRequest, NextResponse } from "next/server";

type UpdateTranslationsBody = {
  siteHost?: string;
  version?: string;
  language?: string;
  pagePath?: string;
  entries?: Record<string, string>;
};

export async function POST(request: NextRequest) {
  let body: UpdateTranslationsBody;
  try {
    body = (await request.json()) as UpdateTranslationsBody;
  } catch {
    return NextResponse.json({ message: "Request body must be valid JSON." }, { status: 400 });
  }

  const siteHost = body.siteHost?.trim();
  const version = body.version?.trim();
  const language = body.language?.trim();
  const pagePath = body.pagePath?.trim();
  if (!siteHost || !version || !language || !pagePath) {
    return NextResponse.json({ message: "siteHost, version, language, and pagePath are required." }, { status: 400 });
  }

  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL("/api/mirror/update-block-translations", backendBaseUrl).toString();

  const auth = request.headers.get("authorization");
  const hasBearer = !!auth && auth.startsWith("Bearer ");
  const backendResponse = await fetch(backendUrl, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      ...(hasBearer ? { Authorization: auth } : {})
    },
    body: JSON.stringify({
      siteHost,
      version,
      language,
      pagePath,
      entries: body.entries ?? {}
    }),
    cache: "no-store"
  });

  let responsePayload: unknown = null;
  try {
    responsePayload = await backendResponse.json();
  } catch {
    responsePayload = null;
  }

  if (!backendResponse.ok) {
    return NextResponse.json(
      responsePayload ?? { message: "Update translations request failed." },
      { status: backendResponse.status }
    );
  }

  return NextResponse.json(responsePayload);
}
