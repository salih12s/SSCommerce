import type { Metadata } from "next";
import { ProductsAdmin } from "@/features/admin/components/ProductsAdmin";

export const metadata: Metadata = { title: "Ürünler" };
export default function AdminProductsPage() { return <ProductsAdmin />; }
