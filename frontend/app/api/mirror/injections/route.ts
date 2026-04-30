import { NextRequest, NextResponse } from "next/server";

export async function GET(request: NextRequest) {
  const siteHost = request.nextUrl.searchParams.get("siteHost")?.trim() ?? "";
  const version = request.nextUrl.searchParams.get("version")?.trim() ?? "";
  if (!siteHost || !version) {
    return NextResponse.json({ message: "siteHost and version are required." }, { status: 400 });
  }

  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL("/api/mirror/injections", backendBaseUrl);
  backendUrl.searchParams.set("siteHost", siteHost);
  backendUrl.searchParams.set("version", version);

  const auth = request.headers.get("authorization");
  const hasBearer = !!auth && auth.startsWith("Bearer ");
  const backendResponse = await fetch(backendUrl.toString(), {
    method: "GET",
    headers: {
      ...(hasBearer ? { Authorization: auth } : {})
    },
    cache: "no-store"
  });

  const payload = await backendResponse.json().catch(() => null);
  if (!backendResponse.ok) {
    return NextResponse.json(payload ?? { message: "Failed to load injection assets." }, { status: backendResponse.status });
  }

  return NextResponse.json(payload);
}

export async function POST(request: NextRequest) {
  const body = await request.json().catch(() => null);
  if (!body || typeof body !== "object") {
    return NextResponse.json({ message: "Request body must be valid JSON." }, { status: 400 });
  }

  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL("/api/mirror/injections", backendBaseUrl).toString();
  const auth = request.headers.get("authorization");
  const hasBearer = !!auth && auth.startsWith("Bearer ");

  const backendResponse = await fetch(backendUrl, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      ...(hasBearer ? { Authorization: auth } : {})
    },
    body: JSON.stringify(body),
    cache: "no-store"
  });

  const payload = await backendResponse.json().catch(() => null);
  if (!backendResponse.ok) {
    return NextResponse.json(payload ?? { message: "Create injection asset failed." }, { status: backendResponse.status });
  }

  return NextResponse.json(payload);
}
