import type { Metadata } from "next";
import { WarehousesAdmin } from "@/components/admin/WarehousesAdmin";

export const metadata: Metadata = { title: "Depolar" };
export default function AdminWarehousesPage() { return <WarehousesAdmin />; }
