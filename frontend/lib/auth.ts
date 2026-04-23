const storageKey = "sitemirror_jwt";
const userKey = "sitemirror_user";

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
  return window.localStorage.getItem(storageKey);
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
  const token = getToken();
  if (!token) {
    return {};
  }
  return { Authorization: `Bearer ${token}` };
}
