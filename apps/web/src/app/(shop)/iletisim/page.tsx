import type { Metadata } from "next";
import { contact } from "@/content/legal/contact";
import { LegalDocumentPage } from "@/features/legal/components/LegalDocumentPage";

export const metadata: Metadata = {
  title: contact.title,
  description: contact.description,
  alternates: { canonical: "/iletisim" },
};

export default function Page() {
  return <LegalDocumentPage doc={contact} />;
}
