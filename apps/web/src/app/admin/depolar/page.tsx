import type { Metadata } from "next";
import { WarehousesAdmin } from "@/features/admin/components/WarehousesAdmin";

export const metadata: Metadata = { title: "Depolar" };
export default function AdminWarehousesPage() { return <WarehousesAdmin />; }
