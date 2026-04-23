import { NextRequest, NextResponse } from "next/server";

const backend = () => process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";

export async function POST(request: NextRequest) {
  let body: { userName?: string; phoneNumber?: string | null; password?: string };
  try {
    body = (await request.json()) as typeof body;
  } catch {
    return NextResponse.json({ message: "Invalid JSON" }, { status: 400 });
  }
  const res = await fetch(new URL("/api/auth/register", backend()).toString(), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
    cache: "no-store"
  });
  const text = await res.text();
  try {
    return NextResponse.json(JSON.parse(text), { status: res.status });
  } catch {
    return new NextResponse(text, {
      status: res.status,
      headers: { "content-type": res.headers.get("content-type") ?? "text/plain" }
    });
  }
}
