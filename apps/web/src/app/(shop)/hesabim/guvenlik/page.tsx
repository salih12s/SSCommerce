import type { Metadata } from "next";
import { AccountNav } from "@/features/account/components/AccountNav";
import { SecurityPanel } from "@/features/account/components/SecurityPanel";
export const metadata: Metadata = { title: "Güvenlik", robots: { index: false } };
export default function SecurityPage() { return <><AccountNav /><SecurityPanel /></>; }
