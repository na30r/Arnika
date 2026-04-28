import { NextResponse } from "next/server";
import { adminAuthCookieName } from "../../../../lib/adminAuthShared";

export async function POST() {
  const response = NextResponse.json({ ok: true });
  response.cookies.set(adminAuthCookieName, "", {
    httpOnly: true,
    sameSite: "lax",
    secure: process.env.NODE_ENV === "production",
    path: "/",
    maxAge: 0
  });
  return response;
}
