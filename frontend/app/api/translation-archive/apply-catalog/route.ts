import { NextRequest, NextResponse } from "next/server";

const backend = () => process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";

export async function POST(request: NextRequest) {
  const auth = request.headers.get("authorization");
  if (!auth) {
    return NextResponse.json({ message: "Unauthorized" }, { status: 401 });
  }
  let body: unknown;
  try {
    body = await request.json();
  } catch {
    return NextResponse.json({ message: "Invalid JSON body." }, { status: 400 });
  }
  const res = await fetch(`${backend()}/api/translation-archive/apply-catalog`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: auth
    },
    body: JSON.stringify(body),
    cache: "no-store"
  });
  const data = await res.json().catch(() => ({}));
  return NextResponse.json(data, { status: res.status });
}
