import type { Metadata } from "next";
import { privacy } from "@/content/legal/privacy";
import { LegalDocumentPage } from "@/features/legal/components/LegalDocumentPage";

export const metadata: Metadata = {
  title: privacy.title,
  description: privacy.description,
  alternates: { canonical: "/gizlilik-kvkk" },
};

export default function Page() {
  return <LegalDocumentPage doc={privacy} />;
}
