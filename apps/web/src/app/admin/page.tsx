import type { Metadata } from "next";
import { DashboardView } from "@/features/admin/components/DashboardView";

export const metadata: Metadata = { title: "Özet" };
export default function AdminHomePage() { return <DashboardView />; }
