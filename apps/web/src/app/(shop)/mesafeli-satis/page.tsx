import type { Metadata } from "next";
import { distanceSales } from "@/content/legal/distance-sales";
import { LegalDocumentPage } from "@/features/legal/components/LegalDocumentPage";

export const metadata: Metadata = {
  title: distanceSales.title,
  description: distanceSales.description,
  alternates: { canonical: "/mesafeli-satis" },
};

export default function Page() {
  return <LegalDocumentPage doc={distanceSales} />;
}
