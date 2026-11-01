import type { Metadata } from "next";
import { AddressManager } from "@/components/AddressManager";
export const metadata: Metadata = { title: "Adreslerim", robots: { index: false } };
export default function AddressesPage() { return <AddressManager />; }
