import type { Metadata } from "next";
import { AccountNav } from "@/features/account/components/AccountNav";
import { FavoritesView } from "@/features/favorites/components/FavoritesView";
export const metadata: Metadata = { title: "Favorilerim", robots: { index: false } };
export default function FavoritesPage() { return <><AccountNav /><FavoritesView /></>; }
