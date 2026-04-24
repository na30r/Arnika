"use client";

import { createContext, useContext, useMemo, useState } from "react";
import type { UserPayload } from "@/lib/auth";
import { getStoredUser } from "@/lib/auth";

const SessionContext = createContext<{
  user: UserPayload | null;
  setUser: (u: UserPayload | null) => void;
} | null>(null);

export function SessionProvider({
  initialUser,
  children
}: {
  initialUser: UserPayload | null;
  children: React.ReactNode;
}) {
  const [user, setUser] = useState<UserPayload | null>(() => initialUser ?? getStoredUser());

  const value = useMemo(() => ({ user, setUser }), [user]);

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession() {
  const c = useContext(SessionContext);
  if (!c) {
    throw new Error("useSession must be used within SessionProvider");
  }
  return c;
}
