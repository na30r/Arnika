import { NextResponse } from "next/server";
import { adminAuthCookieName, hardcodedAdminPassword } from "../../../../lib/adminAuthShared";

export async function POST(req: Request) {
  let body: { password?: string } = {};
  try {
    body = (await req.json()) as typeof body;
  } catch {
    return NextResponse.json({ message: "Invalid request body." }, { status: 400 });
  }

  if (!body.password || body.password !== hardcodedAdminPassword) {
    return NextResponse.json({ message: "Invalid password." }, { status: 401 });
  }

  const response = NextResponse.json({ ok: true });
  response.cookies.set(adminAuthCookieName, "1", {
    httpOnly: true,
    sameSite: "lax",
    secure: process.env.NODE_ENV === "production",
    path: "/",
    maxAge: 60 * 60 * 24 * 7
  });
  return response;
}
