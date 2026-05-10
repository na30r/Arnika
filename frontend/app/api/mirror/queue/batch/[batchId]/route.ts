import { NextRequest, NextResponse } from "next/server";

export async function GET(
  request: NextRequest,
  context: { params: Promise<{ batchId: string }> }
) {
  const { batchId } = await context.params;
  if (!batchId?.trim()) {
    return NextResponse.json({ message: "batchId is required." }, { status: 400 });
  }

  const backendBaseUrl = process.env.MIRROR_API_BASE_URL ?? "http://localhost:5196";
  const backendUrl = new URL(
    `/api/mirror/queue/batch/${encodeURIComponent(batchId.trim())}`,
    backendBaseUrl
  ).toString();
  const auth =
    request.headers.get("authorization") ?? request.headers.get("Authorization");
  const hasBearer = !!auth && auth.startsWith("Bearer ");

  const backendResponse = await fetch(backendUrl, {
    method: "GET",
    headers: {
      ...(hasBearer ? { Authorization: auth } : {})
    },
    cache: "no-store"
  });

  let responsePayload: unknown = null;
  try {
    responsePayload = await backendResponse.json();
  } catch {
    responsePayload = null;
  }

  if (!backendResponse.ok) {
    return NextResponse.json(responsePayload ?? { message: "Batch status request failed." }, {
      status: backendResponse.status
    });
  }

  return NextResponse.json(responsePayload);
}
