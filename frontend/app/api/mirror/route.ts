import { NextRequest, NextResponse } from "next/server";

type MirrorRequestBody = {
  url?: string;
  version?: string;
  linkDrillCount?: number;
  languages?: string[];
  doNotTranslateTexts?: string[];
  extraWaitMs?: number;
  autoScroll?: boolean;
  scrollStepPx?: number;
  scrollDelayMs?: number;
  maxScrollRounds?: number;
};

export async function POST(request: NextRequest) {
  let body: MirrorRequestBody;
  try {
    body = (await request.json()) as MirrorRequestBody;
  } catch {
    return NextResponse.json(
      { message: "Request body must be valid JSON." },
      { status: 400 }
    );
  }

  const trimmedUrl = body.url?.trim();
  if (!trimmedUrl) {
    return NextResponse.json(
      { message: "URL is required." },
      { status: 400 }
    );
  }
  const version = body.version?.trim() || "latest";

  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL("/api/mirror", backendBaseUrl).toString();
  const auth = request.headers.get("authorization");
  const hasBearer = !!auth && auth.startsWith("Bearer ");
  const payload = {
    url: trimmedUrl,
    version,
    linkDrillCount: body.linkDrillCount ?? 0,
    languages: body.languages,
    doNotTranslateTexts: body.doNotTranslateTexts,
    extraWaitMs: body.extraWaitMs ?? 4000,
    autoScroll: body.autoScroll ?? true,
    scrollStepPx: body.scrollStepPx ?? 1200,
    scrollDelayMs: body.scrollDelayMs ?? 150,
    maxScrollRounds: body.maxScrollRounds ?? 20
  };

  const backendResponse = await fetch(backendUrl, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      ...(hasBearer ? { Authorization: auth } : {})
    },
    body: JSON.stringify(payload),
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
      responsePayload ?? { message: "Mirror API request failed." },
      { status: backendResponse.status }
    );
  }

  return NextResponse.json(responsePayload);
}
