import type { Metadata } from "next";
import { DashboardView } from "@/components/admin/DashboardView";

export const metadata: Metadata = { title: "Özet" };
export default function AdminHomePage() { return <DashboardView />; }
