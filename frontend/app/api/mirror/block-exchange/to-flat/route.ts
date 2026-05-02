import { NextRequest, NextResponse } from "next/server";

type ToFlatBody = {
  blockPageJson?: string | null;
  siteHost?: string;
  version?: string;
  pagePath?: string;
  useOriginalWhenTranslatedEmpty?: boolean;
};

export async function POST(request: NextRequest) {
  let body: ToFlatBody;
  try {
    body = (await request.json()) as ToFlatBody;
  } catch {
    return NextResponse.json({ message: "Request body must be valid JSON." }, { status: 400 });
  }

  const hasInlineDoc = typeof body.blockPageJson === "string" && body.blockPageJson.trim().length > 0;
  const hasMirror =
    body.siteHost?.trim() && body.version?.trim() && body.pagePath?.trim();
  if (!hasInlineDoc && !hasMirror) {
    return NextResponse.json(
      { message: "Provide blockPageJson or siteHost, version, and pagePath." },
      { status: 400 }
    );
  }

  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL("/api/mirror/block-exchange/to-flat", backendBaseUrl).toString();
  const auth = request.headers.get("authorization");
  const hasBearer = !!auth && auth.startsWith("Bearer ");

  const backendResponse = await fetch(backendUrl, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      ...(hasBearer ? { Authorization: auth } : {})
    },
    body: JSON.stringify({
      blockPageJson: hasInlineDoc ? body.blockPageJson : null,
      siteHost: body.siteHost?.trim(),
      version: body.version?.trim(),
      pagePath: body.pagePath?.trim(),
      // Default true when omitted; explicit false keeps translation values empty in the flat map.
      useOriginalWhenTranslatedEmpty: body.useOriginalWhenTranslatedEmpty ?? true
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
      responsePayload ?? { message: "To-flat request failed." },
      { status: backendResponse.status }
    );
  }

  return NextResponse.json(responsePayload);
}
