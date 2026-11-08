import type { Metadata } from "next";
import { AccountNav } from "@/features/account/components/AccountNav";
import { AccountOverview } from "@/features/account/components/AccountOverview";
export const metadata: Metadata = { title: "Hesabım" };
export default function AccountPage() { return <><AccountNav /><AccountOverview /></>; }
