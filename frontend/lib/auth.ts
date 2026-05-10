const storageKey = "sitemirror_jwt";
const userKey = "sitemirror_user";

function decodeJwtPayload(token: string): { exp?: number } | null {
  const parts = token.split(".");
  if (parts.length < 2) {
    return null;
  }
  try {
    const base64 = parts[1].replace(/-/g, "+").replace(/_/g, "/");
    const pad = (4 - (base64.length % 4)) % 4;
    const json = atob(base64 + "=".repeat(pad));
    return JSON.parse(json) as { exp?: number };
  } catch {
    return null;
  }
}

/**
 * Returns the stored JWT only if it is not clearly expired (by `exp` claim).
 * If the token is unparsable or expired, clears local session so API calls are not sent with a bad Bearer (which yields 401).
 */
export function getActiveAuthToken(): string | null {
  const token = getToken();
  if (!token) {
    return null;
  }
  const payload = decodeJwtPayload(token);
  if (!payload) {
    clearSession();
    return null;
  }
  if (typeof payload.exp === "number") {
    const nowSec = Math.floor(Date.now() / 1000);
    if (payload.exp <= nowSec + 30) {
      clearSession();
      return null;
    }
  }
  return token;
}

export type UserPayload = {
  userId: string;
  userName: string;
  phoneNumber: string | null;
  subscriptionEndDateUtc: string | null;
};

export function getToken(): string | null {
  if (typeof window === "undefined") {
    return null;
  }
  const raw = window.localStorage.getItem(storageKey);
  const trimmed = raw?.trim();
  return trimmed || null;
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

export function saveSession(token: string, user: UserPayload): void {
  window.localStorage.setItem(storageKey, token);
  window.localStorage.setItem(userKey, JSON.stringify(user));
}

export function clearSession(): void {
  window.localStorage.removeItem(storageKey);
  window.localStorage.removeItem(userKey);
}

export function authHeaders(): HeadersInit {
  const token = getActiveAuthToken();
  if (!token) {
    return {};
  }
  return { Authorization: `Bearer ${token}` };
}
