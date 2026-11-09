import { expect, test } from "@playwright/test";
import { formatPriceMinor } from "../../src/lib/format";
import { addressApi, cartApi, fixture, getCart, loadVariants, registerApi, variantRow, withStock, type Variant } from "./support";

// Bu dosya KATALOG fiyat/stok/aktiflik fixture'ını değiştirir. Ayrı Playwright projesinde ("mutating"), ana proje bittikten sonra
// ve tek worker'da seri koşar; her test değiştirdiği değeri finally ile fixture kuralındaki ilk hâline döndürür.
test.describe.configure({ mode: "serial" });
test.use({ viewport: { width: 1440, height: 900 } });

let variants: Variant[];
test.beforeAll(async ({ request }) => { variants = await loadVariants(request); });

const line = (page: import("@playwright/test").Page) => page.getByTestId("cart-line");

test("stok sıfırlanınca satır 'stokta yok' olur; sepet uyarır, özet devam ettirmez", async ({ page, request, baseURL }) => {
  const v = withStock(variants, 12, 1);
  try {
    await registerApi(page.request, baseURL!);
    await cartApi(page.request, baseURL!, v.id, 2);
    await addressApi(page.request, baseURL!);
    await fixture(request, "stock", { sku: v.sku, quantity: 0 });
    await page.goto("/sepet");
    await expect(line(page)).toHaveAttribute("data-status", "out_of_stock");
    await expect(page.getByTestId("line-note")).toContainText("stokta görünmüyor");
    await expect(page.getByTestId("line-note")).toContainText("sepet stok ayırmaz");
    await expect(page.getByTestId("cart-issues")).toBeVisible();
    const cart = await getCart(page.request);
    expect(cart.hasIssues).toBe(true);
    await expect(page.getByTestId("subtotal")).toHaveText(formatPriceMinor(cart.subtotalMinor));

    await page.goto("/checkout");
    const preview = page.getByTestId("preview");
    await expect(preview).toHaveAttribute("data-can-proceed", "false");
    const issues = page.getByTestId("preview-issues");
    await expect(issues).toContainText("Stokta görünmeyen satır var");
    await expect(issues.getByRole("link", { name: "Sepete dön" })).toHaveAttribute("href", "/sepet");
    await expect(page.getByTestId("order-submit")).toHaveAttribute("aria-disabled", "true");
    await expect(page.getByTestId("preview-shipping")).toHaveText("Kargo henüz hesaplanmadı");

    // Ürün sayfası da aynı gerçeği söyler ve ekleme sunulmaz
    await page.goto(`/urun/${v.productSlug}`);
    const row = variantRow(page, v);
    await expect(row.locator("[data-stock]")).toHaveText("Stokta yok");
    await expect(row.getByRole("radio")).toBeDisabled();
  } finally { await fixture(request, "stock", { sku: v.sku, quantity: v.stock }); }
});

test("stok adetten az kalınca satır 'yetersiz stok' olur ve azaltılabilir; artırılamaz", async ({ page, request, baseURL }) => {
  const v = withStock(variants, 12, 2);
  try {
    await registerApi(page.request, baseURL!);
    await cartApi(page.request, baseURL!, v.id, 5);
    await fixture(request, "stock", { sku: v.sku, quantity: 2 });
    await page.goto("/sepet");
    await expect(line(page)).toHaveAttribute("data-status", "insufficient_stock");
    await expect(page.getByTestId("line-note")).toContainText("en fazla 2 adet");
    await expect(line(page).getByRole("button", { name: /adedi: artır/ })).toBeDisabled();
    await line(page).getByRole("button", { name: /adedi: azalt/ }).click();
    await expect(line(page).getByRole("group")).toContainText("4");
    expect((await getCart(page.request)).items[0].quantity).toBe(4);
  } finally { await fixture(request, "stock", { sku: v.sku, quantity: v.stock }); }
});

test("sayfa açıkken stok düşerse ekleme sunucuda 409 olur ve gerçek adet söylenir", async ({ page, request, baseURL }) => {
  const v = withStock(variants, 3, 1);
  try {
    await registerApi(page.request, baseURL!);
    await page.goto(`/urun/${v.productSlug}`);
    const row = variantRow(page, v);
    await expect(row.locator("[data-stock]")).toHaveText("Son 3 adet");
    await row.getByRole("radio").check();
    const stepper = page.getByRole("group", { name: "Adet" });
    for (let i = 0; i < 2; i++) await stepper.getByRole("button", { name: "Adet: artır" }).click();
    await expect(stepper.getByRole("button", { name: "Adet: artır" })).toBeDisabled(); // görünür stok 3 ile sınırlı
    await fixture(request, "stock", { sku: v.sku, quantity: 1 });
    await page.getByRole("button", { name: "Sepete ekle" }).click();
    await expect(page.locator("main").getByRole("alert")).toContainText("en fazla 1 adet");
    expect((await getCart(page.request)).itemCount).toBe(0);
    await page.reload();
    await expect(variantRow(page, v).locator("[data-stock]")).toHaveText("Son 1 adet");
  } finally { await fixture(request, "stock", { sku: v.sku, quantity: v.stock }); }
});

test("fiyat değişince sepet eski ve yeni fiyatı gösterir; tutarlar sunucudan gelir", async ({ page, request, baseURL }) => {
  const v = withStock(variants, 12, 3);
  const raised = v.priceMinor + 12345;
  try {
    await registerApi(page.request, baseURL!);
    await cartApi(page.request, baseURL!, v.id, 2);
    await addressApi(page.request, baseURL!);
    await fixture(request, "price", { sku: v.sku, priceMinor: raised });
    await page.goto("/sepet");
    const changed = page.getByTestId("price-changed");
    await expect(changed).toContainText(formatPriceMinor(v.priceMinor));
    await expect(changed).toContainText(formatPriceMinor(raised));
    await expect(changed).toContainText("önceki fiyat");
    await expect(page.getByTestId("subtotal")).toHaveText(formatPriceMinor(raised * 2));
    await expect(page.getByTestId("line-total")).toContainText(formatPriceMinor(raised * 2));
    expect((await getCart(page.request)).subtotalMinor).toBe(raised * 2);

    await page.goto("/checkout");
    await expect(page.getByTestId("preview-subtotal")).toHaveText(formatPriceMinor(raised * 2));
    await expect(page.getByTestId("preview-line")).toContainText("Fiyat değişti");
    // Fiyat değişimi bilgilendiricidir: engel değil
    await expect(page.getByTestId("preview")).toHaveAttribute("data-can-proceed", "true");
  } finally { await fixture(request, "price", { sku: v.sku, priceMinor: v.priceMinor }); }
});

test("pasif varyant: satır 'satışta değil', fiyatsız ve ara toplama girmez; özet devam ettirmez", async ({ page, request, baseURL }) => {
  const keep = withStock(variants, 12, 4);
  const off = withStock(variants, 50, 0);
  try {
    await registerApi(page.request, baseURL!);
    await cartApi(page.request, baseURL!, keep.id, 1);
    await cartApi(page.request, baseURL!, off.id, 2);
    await addressApi(page.request, baseURL!);
    await fixture(request, "active", { sku: off.sku, isActive: false });
    await page.goto("/sepet");
    const gone = page.locator("[data-testid='cart-line'][data-status='unavailable']");
    await expect(gone).toHaveCount(1);
    await expect(gone).toContainText("Fiyat yok");
    await expect(gone).toContainText("artık satışta değil");
    await expect(gone.getByTestId("line-total")).toContainText("—");
    await expect(page.getByTestId("subtotal")).toHaveText(formatPriceMinor(keep.priceMinor));
    await expect(page.getByText("Satışta olmayan satırlar ara toplama girmez")).toBeVisible();
    // Kaldırma her zaman mümkündür
    await gone.getByRole("button", { name: /satırını kaldır/ }).click();
    await expect(line(page)).toHaveCount(1);
    await cartApi(page.request, baseURL!, keep.id, 1);
    // Pasif varyant sepete eklenemez
    const response = await page.request.post("/api/backend/api/v1/cart/items", { data: { variantId: off.id, quantity: 1 }, headers: { "X-CSRF-TOKEN": (await (await page.request.get("/api/backend/api/v1/auth/csrf")).json()).token, Origin: baseURL! } });
    expect(response.status()).toBe(409);
    expect((await response.json()).code).toBe("variant_inactive");
  } finally { await fixture(request, "active", { sku: off.sku, isActive: true }); }
});
