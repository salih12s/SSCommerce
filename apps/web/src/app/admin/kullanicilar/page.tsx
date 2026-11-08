import type { Metadata } from "next";
import { UsersAdmin } from "@/features/admin/components/UsersAdmin";

export const metadata: Metadata = { title: "Kullanıcılar" };
export default function AdminUsersPage() { return <UsersAdmin />; }
