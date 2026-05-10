import { NextRequest, NextResponse } from "next/server";

const backend = () => process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";

export async function GET(request: NextRequest) {
  const auth = request.headers.get("authorization");
  if (!auth) {
    return NextResponse.json({ message: "Unauthorized" }, { status: 401 });
  }
  const qs = request.nextUrl.searchParams.toString();
  const url = `${backend()}/api/translation-archive${qs ? `?${qs}` : ""}`;
  const res = await fetch(url, {
    headers: { Authorization: auth },
    cache: "no-store"
  });
  const data = await res.json().catch(() => ({}));
  return NextResponse.json(data, { status: res.status });
}
