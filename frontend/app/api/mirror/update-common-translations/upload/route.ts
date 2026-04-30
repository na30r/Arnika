import { NextRequest, NextResponse } from "next/server";

export async function POST(request: NextRequest) {
  const formData = await request.formData();
  const siteHost = (formData.get("siteHost")?.toString() ?? "").trim();
  const version = (formData.get("version")?.toString() ?? "").trim();
  const language = (formData.get("language")?.toString() ?? "fa").trim();
  const file = formData.get("file");

  if (!siteHost || !version || !language) {
    return NextResponse.json(
      { message: "siteHost, version, and language are required." },
      { status: 400 }
    );
  }

  if (!(file instanceof File)) {
    return NextResponse.json(
      { message: "Translation file is required." },
      { status: 400 }
    );
  }

  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL("/api/mirror/update-common-translations/upload", backendBaseUrl).toString();
  const auth = request.headers.get("authorization");
  const hasBearer = !!auth && auth.startsWith("Bearer ");

  const backendForm = new FormData();
  backendForm.set("siteHost", siteHost);
  backendForm.set("version", version);
  backendForm.set("language", language);
  backendForm.set("file", file, file.name);

  const backendResponse = await fetch(backendUrl, {
    method: "POST",
    headers: {
      ...(hasBearer ? { Authorization: auth } : {})
    },
    body: backendForm,
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
      responsePayload ?? { message: "Apply common translations request failed." },
      { status: backendResponse.status }
    );
  }

  return NextResponse.json(responsePayload);
}
