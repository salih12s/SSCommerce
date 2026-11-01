import type { Metadata } from "next";
import { AccountPanel } from "@/components/AccountPanel";
export const metadata: Metadata = { title: "Hesabım" };
export default function AccountPage() { return <div className="mx-auto max-w-2xl px-4 py-12 sm:px-6"><h1 className="font-display text-title font-extrabold">Hesabım</h1><AccountPanel /></div>; }
