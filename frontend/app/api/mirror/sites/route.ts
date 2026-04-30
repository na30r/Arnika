import { NextRequest, NextResponse } from "next/server";

export async function GET(request: NextRequest) {
  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL("/api/mirror/sites", backendBaseUrl).toString();
  const auth = request.headers.get("authorization");
  const hasBearer = !!auth && auth.startsWith("Bearer ");

  const backendResponse = await fetch(backendUrl, {
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
      responsePayload ?? { message: "Failed to load crawled sites." },
      { status: backendResponse.status }
    );
  }

  return NextResponse.json(responsePayload);
}
