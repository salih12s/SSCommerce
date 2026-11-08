import type { Metadata } from "next";
import { ReportsAdmin } from "@/features/admin/components/ReportsAdmin";

export const metadata: Metadata = { title: "Raporlar" };
export default function AdminReportsPage() { return <ReportsAdmin />; }
