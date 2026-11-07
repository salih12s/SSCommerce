import type { Metadata } from "next";
import { ProductEditor } from "@/components/admin/ProductEditor";

export const metadata: Metadata = { title: "Yeni ürün" };
export default function AdminNewProductPage() { return <ProductEditor id={null} />; }
