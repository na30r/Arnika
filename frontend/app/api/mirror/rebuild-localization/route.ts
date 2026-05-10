import { NextRequest, NextResponse } from "next/server";

type Body = {
  siteHost?: string;
  version?: string;
  refreshI18nFromHtml?: boolean;
  languages?: string[];
  doNotTranslateTexts?: string[];
  generalTranslationClasses?: string[];
};

export async function POST(request: NextRequest) {
  let body: Body;
  try {
    body = (await request.json()) as Body;
  } catch {
    return NextResponse.json({ message: "Request body must be valid JSON." }, { status: 400 });
  }

  const siteHost = body.siteHost?.trim();
  const version = body.version?.trim();
  if (!siteHost || !version) {
    return NextResponse.json({ message: "siteHost and version are required." }, { status: 400 });
  }

  const langs = (body.languages ?? []).map((l) => l.trim().toLowerCase()).filter(Boolean);
  if (langs.length === 0) {
    return NextResponse.json({ message: "languages must include at least one language code." }, { status: 400 });
  }

  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL("/api/mirror/rebuild-localization", backendBaseUrl).toString();
  const auth = request.headers.get("authorization") ?? request.headers.get("Authorization");
  const hasBearer = !!auth && auth.startsWith("Bearer ");

  const backendResponse = await fetch(backendUrl, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      ...(hasBearer ? { Authorization: auth } : {}),
    },
    body: JSON.stringify({
      siteHost,
      version,
      refreshI18nFromHtml: body.refreshI18nFromHtml ?? true,
      languages: langs,
      doNotTranslateTexts: body.doNotTranslateTexts,
      generalTranslationClasses: body.generalTranslationClasses,
    }),
    cache: "no-store",
  });

  let responsePayload: unknown = null;
  try {
    responsePayload = await backendResponse.json();
  } catch {
    responsePayload = null;
  }

  if (!backendResponse.ok) {
    return NextResponse.json(responsePayload ?? { message: "Rebuild localization failed." }, {
      status: backendResponse.status,
    });
  }

  return NextResponse.json(responsePayload);
}
