import type { Metadata } from "next";
import { cookies } from "@/content/legal/cookies";
import { LegalDocumentPage } from "@/features/legal/components/LegalDocumentPage";

export const metadata: Metadata = {
  title: cookies.title,
  description: cookies.description,
  alternates: { canonical: "/cerez-politikasi" },
};

export default function Page() {
  return <LegalDocumentPage doc={cookies} />;
}
