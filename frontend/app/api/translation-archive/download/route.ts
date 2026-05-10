import { NextRequest, NextResponse } from "next/server";

const backend = () => process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";

export async function GET(request: NextRequest) {
  const auth = request.headers.get("authorization");
  if (!auth) {
    return NextResponse.json({ message: "Unauthorized" }, { status: 401 });
  }
  const qs = request.nextUrl.searchParams.toString();
  const url = `${backend()}/api/translation-archive/download${qs ? `?${qs}` : ""}`;
  const res = await fetch(url, {
    headers: { Authorization: auth },
    cache: "no-store"
  });
  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    return NextResponse.json(err, { status: res.status });
  }
  const buf = await res.arrayBuffer();
  const ct = res.headers.get("content-type") ?? "application/json; charset=utf-8";
  const cd = res.headers.get("content-disposition");
  const headers = new Headers();
  headers.set("Content-Type", ct);
  if (cd) headers.set("Content-Disposition", cd);
  return new NextResponse(buf, { status: 200, headers });
}
