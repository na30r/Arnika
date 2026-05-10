import { NextRequest, NextResponse } from "next/server";

const backend = () => process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";

export async function GET(request: NextRequest) {
  const auth = request.headers.get("authorization");
  if (!auth) {
    return NextResponse.json({ message: "Unauthorized" }, { status: 401 });
  }
  const url = new URL("/api/logs", backend());
  const level = request.nextUrl.searchParams.get("level");
  const take = request.nextUrl.searchParams.get("take");
  if (level) url.searchParams.set("level", level);
  if (take) url.searchParams.set("take", take);

  const res = await fetch(url.toString(), {
    headers: { Authorization: auth },
    cache: "no-store"
  });
  const data = await res.json().catch(() => ({}));
  return NextResponse.json(data, { status: res.status });
}
