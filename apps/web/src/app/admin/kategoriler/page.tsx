import type { Metadata } from "next";
import { CategoriesAdmin } from "@/features/admin/components/CategoriesAdmin";

export const metadata: Metadata = { title: "Kategoriler" };
export default function AdminCategoriesPage() { return <CategoriesAdmin />; }
