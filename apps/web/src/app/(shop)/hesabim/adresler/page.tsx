import type { Metadata } from "next";
import { AccountNav } from "@/features/account/components/AccountNav";
import { AddressManager } from "@/features/account/components/AddressManager";
export const metadata: Metadata = { title: "Adreslerim", robots: { index: false } };
export default function AddressesPage() { return <><AccountNav /><AddressManager /></>; }
