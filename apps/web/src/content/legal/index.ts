import { about } from "./about";
import { contact } from "./contact";
import { cookies } from "./cookies";
import { distanceSales } from "./distance-sales";
import { privacy } from "./privacy";
import type { LegalDocument } from "./types";

export { LAST_UPDATED, SAMPLE_NOTICE } from "./types";
export type { LegalBlock, LegalDocument, LegalSection } from "./types";

/** Sıra alt bilgideki "Kurumsal" sütununun sırasıdır. */
export const legalDocuments: readonly LegalDocument[] = [about, contact, privacy, distanceSales, cookies];

export const legalLinks = legalDocuments.map((d) => ({ href: `/${d.slug}`, label: d.navLabel }));
