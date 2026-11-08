import type { Metadata } from "next";
import { about } from "@/content/legal/about";
import { LegalDocumentPage } from "@/features/legal/components/LegalDocumentPage";

export const metadata: Metadata = {
  title: about.title,
  description: about.description,
  alternates: { canonical: "/hakkimizda" },
};

export default function Page() {
  return <LegalDocumentPage doc={about} />;
}
