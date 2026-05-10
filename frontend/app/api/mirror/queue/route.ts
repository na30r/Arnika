import { NextRequest, NextResponse } from "next/server";

type MirrorQueueBody = {
  urls?: string[];
  version?: string;
  linkDrillCount?: number;
  crawlUrlAllowPrefixes?: string[];
  crawlUrlDenyPrefixes?: string[];
  languages?: string[];
  doNotTranslateTexts?: string[];
  generalTranslationClasses?: string[];
  extraWaitMs?: number;
  autoScroll?: boolean;
  scrollStepPx?: number;
  scrollDelayMs?: number;
  maxScrollRounds?: number;
};

export async function POST(request: NextRequest) {
  let body: MirrorQueueBody;
  try {
    body = (await request.json()) as MirrorQueueBody;
  } catch {
    return NextResponse.json({ message: "Request body must be valid JSON." }, { status: 400 });
  }

  const urls = (body.urls ?? [])
    .map((u) => u.trim())
    .filter((u) => u.length > 0);
  if (urls.length === 0) {
    return NextResponse.json({ message: "At least one URL is required." }, { status: 400 });
  }

  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL("/api/mirror/queue", backendBaseUrl).toString();
  const auth =
    request.headers.get("authorization") ?? request.headers.get("Authorization");
  const hasBearer = !!auth && auth.startsWith("Bearer ");

  const payload = {
    urls,
    version: body.version?.trim() || "latest",
    linkDrillCount: body.linkDrillCount ?? 0,
    crawlUrlAllowPrefixes: body.crawlUrlAllowPrefixes,
    crawlUrlDenyPrefixes: body.crawlUrlDenyPrefixes,
    languages: body.languages,
    doNotTranslateTexts: body.doNotTranslateTexts,
    generalTranslationClasses: body.generalTranslationClasses,
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
    return NextResponse.json(responsePayload ?? { message: "Mirror queue API request failed." }, {
      status: backendResponse.status
    });
  }

  return NextResponse.json(responsePayload);
}
