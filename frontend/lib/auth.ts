import { PUBLIC_SIG_COOKIE } from "./authConstants";

const storageKey = "sitemirror_jwt";
const userKey = "sitemirror_user";

export type UserPayload = {
  userId: string;
  userName: string;
  phoneNumber: string | null;
  subscriptionEndDateUtc: string | null;
};

function getCookieValue(name: string): string | null {
  if (typeof document === "undefined") {
    return null;
  }
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const match = document.cookie.match(new RegExp(`(?:^|;\\s*)${escaped}=([^;]*)`));
  return match?.[1] ? decodeURIComponent(match[1].trim()) : null;
}

/**
 * For API `Authorization: Bearer` — full JWT from localStorage after login, or the
 * public HMAC (pairs with the httpOnly session) after `/api/auth/session` syncs cookies.
 */
export function getToken(): string | null {
  if (typeof window === "undefined") {
    return null;
  }
  return window.localStorage.getItem(storageKey) ?? getCookieValue(PUBLIC_SIG_COOKIE);
}

export function getStoredUser(): UserPayload | null {
  if (typeof window === "undefined") {
    return null;
  }
  const raw = window.localStorage.getItem(userKey);
  if (!raw) {
    return null;
  }
  try {
    return JSON.parse(raw) as UserPayload;
  } catch {
    return null;
  }
}

function clearClientCookie(name: string) {
  document.cookie = `${name}=; path=/; max-age=0`;
}

/**
 * Persists the session to localStorage and httpOnly + signed cookies (server also validates JWT).
 */
export async function saveSession(token: string, user: UserPayload): Promise<void> {
  window.localStorage.setItem(storageKey, token);
  window.localStorage.setItem(userKey, JSON.stringify(user));
  const res = await fetch("/api/auth/session", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ token })
  });
  if (res.ok) {
    return;
  }
  throw new Error("Session cookie sync failed.");
}

export async function clearSession(): Promise<void> {
  window.localStorage.removeItem(storageKey);
  window.localStorage.removeItem(userKey);
  try {
    await fetch("/api/auth/session", { method: "DELETE" });
  } catch {
    /* ignore */
  }
  clearClientCookie(PUBLIC_SIG_COOKIE);
}

export function authHeaders(): HeadersInit {
  const token = getToken();
  if (!token) {
    return {};
  }
  return { Authorization: `Bearer ${token}` };
}
