import { NextRequest, NextResponse } from "next/server";

export async function GET(request: NextRequest) {
  const siteHost = request.nextUrl.searchParams.get("siteHost")?.trim();
  if (!siteHost) {
    return NextResponse.json({ message: "siteHost is required." }, { status: 400 });
  }

  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const u = new URL("/api/mirror/block-catalog/versions", backendBaseUrl);
  u.searchParams.set("siteHost", siteHost);
  const auth = request.headers.get("authorization");
  const hasBearer = !!auth && auth.startsWith("Bearer ");

  const backendResponse = await fetch(u.toString(), {
    method: "GET",
    headers: {
      ...(hasBearer ? { Authorization: auth } : {})
    },
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
      responsePayload ?? { message: "Failed to load versions." },
      { status: backendResponse.status }
    );
  }

  return NextResponse.json(responsePayload);
}
