import { NextRequest, NextResponse } from "next/server";

export async function POST(request: NextRequest) {
  const form = await request.formData();
  const siteHost = String(form.get("siteHost") ?? "").trim();
  const version = String(form.get("version") ?? "").trim();
  const language = String(form.get("language") ?? "").trim();
  const file = form.get("file");
  if (!siteHost || !version || !language) {
    return NextResponse.json({ message: "siteHost, version, and language are required." }, { status: 400 });
  }
  if (!(file instanceof File)) {
    return NextResponse.json({ message: "file is required." }, { status: 400 });
  }

  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL("/api/mirror/update-translations/upload", backendBaseUrl).toString();

  const forward = new FormData();
  forward.set("siteHost", siteHost);
  forward.set("version", version);
  forward.set("language", language);
  forward.set("file", file, file.name);

  for (const value of form.getAll("targetPages")) {
    const v = String(value ?? "").trim();
    if (v) {
      forward.append("targetPages", v);
    }
  }
  for (const value of form.getAll("doNotTranslateTexts")) {
    const v = String(value ?? "").trim();
    if (v) {
      forward.append("doNotTranslateTexts", v);
    }
  }

  const auth = request.headers.get("authorization");
  const hasBearer = !!auth && auth.startsWith("Bearer ");
  const backendResponse = await fetch(backendUrl, {
    method: "POST",
    headers: {
      ...(hasBearer ? { Authorization: auth } : {})
    },
    body: forward,
    cache: "no-store"
  });

  let responsePayload: unknown = null;
  try {
    responsePayload = await backendResponse.json();
  } catch {
    responsePayload = null;
  }

  if (!backendResponse.ok) {
    return NextResponse.json(
      responsePayload ?? { message: "Upload translations request failed." },
      { status: backendResponse.status }
    );
  }

  return NextResponse.json(responsePayload);
}
