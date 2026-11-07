import { StoreShell } from "@/components/StoreShell";

export default function ShopLayout({ children }: LayoutProps<"/">) {
  return <StoreShell>{children}</StoreShell>;
}
