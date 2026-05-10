import { NextRequest, NextResponse } from "next/server";

type Body = {
  siteHost?: string;
  version?: string;
  entryRelativePaths?: string[];
  followNavigationalHtml?: boolean;
  protectedPathPrefixes?: string[];
  maxPathsPerList?: number;
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

  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL("/api/mirror/storage-analyze", backendBaseUrl).toString();
  const auth = request.headers.get("authorization") ?? request.headers.get("Authorization");
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
      entryRelativePaths: body.entryRelativePaths,
      followNavigationalHtml: body.followNavigationalHtml ?? false,
      protectedPathPrefixes: body.protectedPathPrefixes,
      maxPathsPerList: body.maxPathsPerList ?? 2000
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
    return NextResponse.json(responsePayload ?? { message: "Storage analyze request failed." }, {
      status: backendResponse.status
    });
  }

  return NextResponse.json(responsePayload);
}
