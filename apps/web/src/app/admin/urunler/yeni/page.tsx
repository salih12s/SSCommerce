import type { Metadata } from "next";
import { ProductEditor } from "@/features/admin/components/ProductEditor";

export const metadata: Metadata = { title: "Yeni ürün" };
export default function AdminNewProductPage() { return <ProductEditor id={null} />; }
