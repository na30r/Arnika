import { NextRequest, NextResponse } from "next/server";

type StorageDedupeBody = {
  siteHost?: string;
  version?: string;
  dryRun?: boolean;
};

export async function POST(request: NextRequest) {
  let body: StorageDedupeBody;
  try {
    body = (await request.json()) as StorageDedupeBody;
  } catch {
    return NextResponse.json({ message: "Request body must be valid JSON." }, { status: 400 });
  }

  const siteHost = body.siteHost?.trim();
  const version = body.version?.trim();
  if (!siteHost || !version) {
    return NextResponse.json({ message: "siteHost and version are required." }, { status: 400 });
  }

  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL("/api/mirror/storage-dedupe", backendBaseUrl).toString();
  const auth =
    request.headers.get("authorization") ?? request.headers.get("Authorization");
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
      dryRun: body.dryRun ?? true
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
    return NextResponse.json(responsePayload ?? { message: "Storage dedupe request failed." }, {
      status: backendResponse.status
    });
  }

  return NextResponse.json(responsePayload);
}
