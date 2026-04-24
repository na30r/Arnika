import { NextRequest, NextResponse } from "next/server";
import { clearSessionResponse, setSessionResponse, verifyToken, jwtToUserPayload } from "@/lib/auth-session";

function jsonError(message: string, status: number) {
  return NextResponse.json({ message }, { status });
}

export async function POST(request: NextRequest) {
  let body: { token?: string };
  try {
    body = (await request.json()) as { token?: string };
  } catch {
    return jsonError("Invalid JSON", 400);
  }
  const token = body.token?.trim();
  if (!token) {
    return jsonError("Token is required", 400);
  }
  try {
    const payload = await verifyToken(token);
    if (!jwtToUserPayload(payload)) {
      return jsonError("Invalid token", 401);
    }
  } catch {
    return jsonError("Invalid or expired token", 401);
  }
  return await setSessionResponse(token);
}

export function DELETE() {
  return clearSessionResponse();
}
