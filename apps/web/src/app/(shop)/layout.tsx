import { StoreShell } from "@/features/shell/components/StoreShell";

export default function ShopLayout({ children }: LayoutProps<"/">) {
  return <StoreShell>{children}</StoreShell>;
}
