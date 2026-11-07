"use client";

import { createContext, useContext } from "react";
import type { Account } from "@/lib/auth-client";

export type AdminSession = { account: Account; isAdmin: boolean };
export const AdminContext = createContext<AdminSession | null>(null);

/** Yönetim sayfaları yalnız AdminShell içinde çizilir; oturum ve rol buradan okunur. */
export function useAdmin(): AdminSession {
  const session = useContext(AdminContext);
  if (!session) throw new Error("useAdmin yalnız AdminShell içinde kullanılabilir.");
  return session;
}
