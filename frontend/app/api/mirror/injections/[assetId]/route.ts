import { NextRequest, NextResponse } from "next/server";

type Params = { params: Promise<{ assetId: string }> };

function withAuth(request: NextRequest): HeadersInit {
  const auth = request.headers.get("authorization");
  if (auth && auth.startsWith("Bearer ")) {
    return { Authorization: auth };
  }

  return {};
}

export async function GET(request: NextRequest, { params }: Params) {
  const { assetId } = await params;
  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL(`/api/mirror/injections/${encodeURIComponent(assetId)}`, backendBaseUrl).toString();
  const backendResponse = await fetch(backendUrl, {
    method: "GET",
    headers: { ...withAuth(request) },
    cache: "no-store"
  });

  const payload = await backendResponse.json().catch(() => null);
  return NextResponse.json(payload, { status: backendResponse.status });
}

export async function PUT(request: NextRequest, { params }: Params) {
  const { assetId } = await params;
  const body = await request.json().catch(() => null);
  if (!body || typeof body !== "object") {
    return NextResponse.json({ message: "Request body must be valid JSON." }, { status: 400 });
  }

  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL(`/api/mirror/injections/${encodeURIComponent(assetId)}`, backendBaseUrl).toString();
  const backendResponse = await fetch(backendUrl, {
    method: "PUT",
    headers: {
      "Content-Type": "application/json",
      ...withAuth(request)
    },
    body: JSON.stringify(body),
    cache: "no-store"
  });

  const payload = await backendResponse.json().catch(() => null);
  return NextResponse.json(payload, { status: backendResponse.status });
}

export async function DELETE(request: NextRequest, { params }: Params) {
  const { assetId } = await params;
  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL(`/api/mirror/injections/${encodeURIComponent(assetId)}`, backendBaseUrl).toString();
  const backendResponse = await fetch(backendUrl, {
    method: "DELETE",
    headers: { ...withAuth(request) },
    cache: "no-store"
  });

  if (backendResponse.status === 204) {
    return new NextResponse(null, { status: 204 });
  }

  const payload = await backendResponse.json().catch(() => null);
  return NextResponse.json(payload, { status: backendResponse.status });
}
