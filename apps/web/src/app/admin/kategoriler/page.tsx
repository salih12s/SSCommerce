import type { Metadata } from "next";
import { CategoriesAdmin } from "@/components/admin/CategoriesAdmin";

export const metadata: Metadata = { title: "Kategoriler" };
export default function AdminCategoriesPage() { return <CategoriesAdmin />; }
