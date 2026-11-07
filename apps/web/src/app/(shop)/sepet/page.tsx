import type { Metadata } from "next";
import { CartView } from "@/components/CartView";
export const metadata: Metadata = { title: "Sepetim", robots: { index: false } };
export default function CartPage() { return <CartView />; }
