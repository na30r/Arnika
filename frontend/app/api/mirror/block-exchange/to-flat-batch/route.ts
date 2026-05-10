import { NextRequest, NextResponse } from "next/server";

type BatchBody = {
  siteHost?: string;
  version?: string;
  pagePaths?: string[];
  useOriginalWhenTranslatedEmpty?: boolean;
};

export async function POST(request: NextRequest) {
  let body: BatchBody;
  try {
    body = (await request.json()) as BatchBody;
  } catch {
    return NextResponse.json({ message: "Request body must be valid JSON." }, { status: 400 });
  }

  if (!body.siteHost?.trim() || !body.version?.trim()) {
    return NextResponse.json({ message: "siteHost and version are required." }, { status: 400 });
  }

  const paths = Array.isArray(body.pagePaths) ? body.pagePaths.map((p) => String(p).trim()).filter(Boolean) : [];
  if (paths.length === 0) {
    return NextResponse.json({ message: "pagePaths must contain at least one path." }, { status: 400 });
  }

  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL("/api/mirror/block-exchange/to-flat-batch", backendBaseUrl).toString();
  const auth = request.headers.get("authorization");
  const hasBearer = !!auth && auth.startsWith("Bearer ");

  const backendResponse = await fetch(backendUrl, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      ...(hasBearer ? { Authorization: auth } : {})
    },
    body: JSON.stringify({
      siteHost: body.siteHost.trim(),
      version: body.version.trim(),
      pagePaths: paths,
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
      responsePayload ?? { message: "Batch to-flat failed." },
      { status: backendResponse.status }
    );
  }

  return NextResponse.json(responsePayload);
}
