import { NextRequest, NextResponse } from "next/server";
import { authHeaderForBackend, getAuthFromRequest } from "@/lib/authFromRequest";

const backend = () => process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";

export async function GET(request: NextRequest) {
  const authz = await getAuthFromRequest(request);
  if (!authz) {
    return NextResponse.json({ message: "Unauthorized" }, { status: 401 });
  }
  const res = await fetch(new URL("/api/auth/profile", backend()).toString(), {
    headers: { ...authHeaderForBackend(authz.token) },
    cache: "no-store"
  });
  const data = await res.json().catch(() => ({}));
  return NextResponse.json(data, { status: res.status });
}
