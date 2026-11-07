import { expect, test } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { formatPriceMinor } from "../../src/lib/format";
import type { AuditRow, Category, ImageAsset, Paged, ProductDetail, ProductSummary } from "../../src/lib/admin/types";
import { actor, aGet, aProblem, aWrite, mkCategory, mkProduct, mkSellable, mkSku, mkVariant, receiveApi, retire, settled, uid, warehouseId, type Actor } from "./admin-support";
import { api, cartApi, goodAddress, send, orderSend } from "./support";

// Faz 8 katalog yönetimi: kategori ağacı, ürün/varyant CRUD, sürüm çakışması, görseller; değişiklik VİTRİNDE görünür (kuruş → TL).
// Bu dosya vitrin kataloğuna yeni kayıtlar ekler: "admin-catalog" projesinde, vitrin projelerinden sonra ve tek worker'da koşar.
// Her test kendi benzersiz kayıtlarını kurar ve `retire` ile pasife alarak (silme yok) paylaşılan kataloğu eski hâline döndürür.
test.use({ viewport: { width: 1440, height: 900 } });

let admin: Actor;
test.beforeAll(async ({ browser, baseURL }) => { admin = await actor(browser, baseURL!, "Admin"); });
test.afterAll(async () => { await admin?.context.close(); });

const page = () => admin.page;
const auditOf = (targetType: string, targetId: string) => aGet<Paged<AuditRow>>(admin.request, `/audit?targetType=${targetType}&targetId=${targetId}&pageSize=50`);
async function open(path: string) { await page().goto(path); await settled(page()); }
const FORBIDDEN_AUDIT_KEYS = ["password", "password_hash", "security_stamp", "token", "email", "phone", "address", "street_address"];

test.describe("kategoriler", () => {
  test("arayüzden kök ve alt kategori oluşturulur (ağaç girintisi), sıra/pasifleştirme çalışır, aynı adres reddedilir", async () => {
    const tag = uid();
    const rootName = `Kök ${tag}`, childName = `Alt ${tag}`;
    const created: Category[] = [];
    try {
      await open("/admin/kategoriler");
      await page().getByRole("button", { name: "Yeni kategori" }).click();
      const form = page().locator("form[aria-busy]");
      await form.getByLabel("Ad", { exact: true }).fill(rootName);
      // Slug önerisi addan türer (Türkçe karakter sadeleştirilir) ve kullanıcı değiştirebilir
      await expect(form.getByLabel("Adres (slug)")).toHaveValue(`kok-${tag}`);
      await form.getByRole("button", { name: "Kategori oluştur" }).click();
      await expect(page().getByText(`“${rootName}” oluşturuldu.`)).toBeVisible();
      const table = page().getByRole("table", { name: "Kategori ağacı" });
      await expect(table.getByRole("row").filter({ hasText: rootName })).toContainText(`kok-${tag}`);

      // Alt kategori: üst seçimi
      await page().getByRole("button", { name: "Yeni kategori" }).click();
      const form2 = page().locator("form[aria-busy]");
      await form2.getByLabel("Ad", { exact: true }).fill(childName);
      await form2.getByLabel("Üst kategori").selectOption({ label: rootName });
      await form2.getByRole("button", { name: "Kategori oluştur" }).click();
      await expect(page().getByText(`“${childName}” oluşturuldu.`)).toBeVisible();
      const childRow = table.getByRole("row").filter({ hasText: childName });
      await expect(childRow).toContainText("alt kategori, düzey 2");

      // Sunucu doğrulaması: aynı adres → 409 category_slug_taken, alanın altında
      await page().getByRole("button", { name: "Yeni kategori" }).click();
      const form3 = page().locator("form[aria-busy]");
      await form3.getByLabel("Ad", { exact: true }).fill(`Başka ${tag}`);
      await form3.getByLabel("Adres (slug)").fill(`kok-${tag}`);
      await form3.getByRole("button", { name: "Kategori oluştur" }).click();
      await expect(form3.locator("#k-slug-error")).toHaveText("Bu kategori adresi (slug) kullanılıyor.");
      await form3.getByRole("button", { name: "Vazgeç" }).click();

      // Kök düzenlenirken üst-kategori listesi kendi alt dalını sunmaz (döngü arayüzden kurulamaz)
      await page().getByRole("button", { name: `Düzenle: ${rootName}` }).click();
      const edit = page().locator("form[aria-busy]");
      const options = await edit.getByLabel("Üst kategori").locator("option").allTextContents();
      expect(options.some((o) => o.includes(rootName))).toBe(false);
      expect(options.some((o) => o.includes(childName))).toBe(false);
      // Sıra değiştir + pasife al
      await edit.getByLabel("Sıra", { exact: true }).fill("7");
      await edit.getByLabel("Satışta (aktif)").uncheck();
      await edit.getByRole("button", { name: "Kaydet" }).click();
      await expect(page().getByText(`“${rootName}” kaydedildi.`)).toBeVisible();
      await expect(table.getByRole("row").filter({ hasText: rootName })).toContainText("Pasif");
      await expect(table.getByRole("row").filter({ hasText: rootName })).toContainText("7");

      const all = await aGet<Category[]>(admin.request, "/categories");
      const root = all.find((c) => c.name === rootName)!;
      const child = all.find((c) => c.name === childName)!;
      created.push(root, child);
      expect(child.parentId).toBe(root.id);
      expect(root.isActive).toBe(false);
      expect(root.version).toBe(2);
      // Denetim: category.create + category.update, yapan yönetici, yasak anahtar yok
      const audit = (await auditOf("category", root.id)).items;
      expect(audit.map((r) => r.action).sort()).toEqual(["category.create", "category.update"]);
      for (const row of audit) { expect(row.actorEmail).toBe(admin.email); for (const key of FORBIDDEN_AUDIT_KEYS) expect(row.changes).not.toHaveProperty(key); }
    } finally {
      for (const c of created) { const fresh = (await aGet<Category[]>(admin.request, "/categories")).find((x) => x.id === c.id); if (fresh?.isActive) await send(admin.request, "PATCH", `/admin/categories/${c.id}`, admin.origin, { version: fresh.version, isActive: false }); }
    }
  });

  test("API: döngü 409 category_cycle, 6. düzey 409 category_too_deep, bilinmeyen üst 404; arayüz derinlik hatasını söyler", async () => {
    const chain: Category[] = [];
    try {
      for (let depth = 1; depth <= 5; depth++) chain.push(await mkCategory(admin, depth === 1 ? {} : { parentId: chain[depth - 2].id }));
      // 5. düzeye çocuk eklenemez
      await aProblem(admin, "POST", "/categories", { name: "Çok derin", slug: `e2e-${uid()}`, parentId: chain[4].id }, 409, "category_too_deep");
      // Kök, kendi torununun altına taşınamaz (döngü); kendisinin altına da
      const root = (await aGet<Category[]>(admin.request, "/categories")).find((c) => c.id === chain[0].id)!;
      await aProblem(admin, "PATCH", `/categories/${root.id}`, { version: root.version, parentId: chain[3].id }, 409, "category_cycle");
      await aProblem(admin, "PATCH", `/categories/${root.id}`, { version: root.version, parentId: root.id }, 409, "category_cycle").catch(async () => {
        // kendi altına taşıma da döngü ya da doğrulama hatası olabilir; yine de reddedilmeli
        const response = await send(admin.request, "PATCH", `/admin/categories/${root.id}`, admin.origin, { version: root.version, parentId: root.id });
        expect([400, 409]).toContain(response.status());
      });
      await aProblem(admin, "POST", "/categories", { name: "Yetim", slug: `e2e-${uid()}`, parentId: randomUUID() }, 404, "category_not_found");
      // Reddedilen yazma sürümü artırmadı
      expect((await aGet<Category[]>(admin.request, "/categories")).find((c) => c.id === root.id)!.version).toBe(root.version);

      // Arayüz: en derin kategoriyi üst seçip kaydet → sunucunun derinlik mesajı görünür
      await open("/admin/kategoriler");
      await page().getByRole("button", { name: "Yeni kategori" }).click();
      const form = page().locator("form[aria-busy]");
      await form.getByLabel("Ad", { exact: true }).fill("Altıncı düzey");
      await form.getByLabel("Üst kategori").selectOption({ value: chain[4].id });
      await form.getByRole("button", { name: "Kategori oluştur" }).click();
      await expect(form.getByRole("alert").filter({ hasText: "Kategori ağacı en çok 5 düzey olabilir." })).toBeVisible();
      // Derinlik ve döngü hataları listede kayıt bırakmadı
      expect((await aGet<Category[]>(admin.request, "/categories")).some((c) => c.name === "Altıncı düzey")).toBe(false);
    } finally {
      const all = await aGet<Category[]>(admin.request, "/categories");
      for (const c of chain) { const fresh = all.find((x) => x.id === c.id); if (fresh?.isActive) await send(admin.request, "PATCH", `/admin/categories/${c.id}`, admin.origin, { version: fresh.version, isActive: false }); }
    }
  });

  test("sürüm çakışması: başkası araya girince arayüz 'sizden sonra değişti' der, üzerine yazmaz; güncel kaydı yükleyince yeniden kaydedilir", async () => {
    const category = await mkCategory(admin);
    try {
      await open("/admin/kategoriler");
      await page().getByRole("button", { name: `Düzenle: ${category.name}` }).click();
      const form = page().locator("form[aria-busy]");
      await expect(form.getByLabel("Ad", { exact: true })).toHaveValue(category.name);
      // Başka bir oturum (burada API) aynı kaydı değiştirir
      const other = await aWrite<Category>(admin, "PATCH", `/categories/${category.id}`, { version: category.version, name: `${category.name} (başkası)` }, 200);
      expect(other.version).toBe(category.version + 1);
      await form.getByLabel("Sıra", { exact: true }).fill("9");
      await form.getByRole("button", { name: "Kaydet" }).click();
      await expect(page().getByText("Bu kayıt sizden sonra değişti")).toBeVisible();
      // Başkasının değişikliği ezilmedi
      expect((await aGet<Category[]>(admin.request, "/categories")).find((c) => c.id === category.id)).toMatchObject({ name: `${category.name} (başkası)`, sortOrder: category.sortOrder, version: category.version + 1 });
      // Sözleşme: 409 gövdesi currentVersion taşır
      const problem = await aProblem(admin, "PATCH", `/categories/${category.id}`, { version: category.version, sortOrder: 9 }, 409, "version_conflict");
      expect(problem.currentVersion).toBe(category.version + 1);
      // Güncel kaydı yükle → form yeni sürümle yenilenir; ikinci kayıt başarılı
      await page().getByRole("button", { name: "Güncel kaydı yükle" }).click();
      await expect(form.getByLabel("Ad", { exact: true })).toHaveValue(`${category.name} (başkası)`);
      await expect(page().getByText("Bu kayıt sizden sonra değişti")).toHaveCount(0);
      await form.getByLabel("Sıra", { exact: true }).fill("9");
      await form.getByRole("button", { name: "Kaydet" }).click();
      await expect(page().getByText(`“${category.name} (başkası)” kaydedildi.`)).toBeVisible();
      expect((await aGet<Category[]>(admin.request, "/categories")).find((c) => c.id === category.id)).toMatchObject({ sortOrder: 9, version: category.version + 2 });
    } finally { await retire(admin, { product: { id: randomUUID() }, category }); }
  });

  test("aynı sürümle iki PATCH: biri 200, öteki 409; değişiklik yapmayan yazma sürümü artırmaz ve denetim satırı yazmaz", async () => {
    const category = await mkCategory(admin);
    try {
      const results = await Promise.all([1, 2].map((n) => send(admin.request, "PATCH", `/admin/categories/${category.id}`, admin.origin, { version: category.version, name: `${category.name} ${n}` })));
      expect(results.map((r) => r.status()).sort()).toEqual([200, 409]);
      const now = (await aGet<Category[]>(admin.request, "/categories")).find((c) => c.id === category.id)!;
      expect(now.version).toBe(category.version + 1);
      const auditBefore = (await auditOf("category", category.id)).totalCount;
      const same = await aWrite<Category>(admin, "PATCH", `/categories/${category.id}`, { version: now.version, name: now.name }, 200);
      expect(same.version).toBe(now.version);
      expect((await auditOf("category", category.id)).totalCount).toBe(auditBefore);
    } finally { await retire(admin, { product: { id: randomUUID() }, category }); }
  });
});

test.describe("ürün ve varyant", () => {
  test("arayüzde ürün oluştur → varyant ekle (TL → kuruş) → stok gir → satışa aç: vitrinde doğru TL fiyatıyla görünür; fiyat değişir; pasife alınca kalkar", async ({ browser }) => {
    const category = await mkCategory(admin);
    const name = `Deneme Telefonu ${uid()}`;
    let productId = "";
    try {
      await open("/admin/urunler/yeni");
      const form = page().locator("form[aria-busy]").first();
      await form.getByLabel("Ad", { exact: true }).fill(name);
      const slug = await form.getByLabel("Adres (slug)").inputValue();
      expect(slug).toMatch(/^deneme-telefonu-[a-z0-9]+$/);
      await form.getByLabel("Kategori", { exact: true }).selectOption({ value: category.id });
      await form.getByLabel("Marka").fill("SSC Test");
      await form.getByLabel("Açıklama").fill("E2E ile oluşturulan deneme ürünü.");
      await form.getByLabel("Özellikler").fill("Ekran: 6,1 inç\nBellek: 128 GB");
      await form.getByRole("button", { name: "Ürünü oluştur" }).click();
      await expect(page()).toHaveURL(/\/admin\/urunler\/[0-9a-f-]{36}$/);
      productId = page().url().split("/").pop()!;
      await settled(page());
      await expect(page().getByRole("heading", { level: 1, name })).toBeVisible();
      await expect(page().getByText("Sürüm 1")).toBeVisible();
      // Ürün önce PASİF oluşturulur ve vitrinde yok
      const detail = await aGet<ProductDetail>(admin.request, `/products/${productId}`);
      expect(detail).toMatchObject({ isActive: false, isDemo: false, brand: "SSC Test", specifications: { Ekran: "6,1 inç", Bellek: "128 GB" } });
      expect((await browser.newContext({ baseURL: admin.origin }).then(async (c) => { const r = await c.request.get(`${api}/products/${slug}`); await c.close(); return r.status(); }))).toBe(404);

      // Aynı adresle ikinci ürün: alan hatası
      await open("/admin/urunler/yeni");
      const dup = page().locator("form[aria-busy]").first();
      await dup.getByLabel("Ad", { exact: true }).fill(`${name} kopya`);
      await dup.getByLabel("Adres (slug)").fill(slug);
      await dup.getByLabel("Kategori", { exact: true }).selectOption({ value: category.id });
      await dup.getByRole("button", { name: "Ürünü oluştur" }).click();
      await expect(dup.locator("#u-slug-error")).toHaveText("Bu ürün adresi (slug) kullanılıyor.");

      // Varyant: TL girdisi kuruşa çevrilir ("1.299,90" → 129990)
      await open(`/admin/urunler/${productId}`);
      const sku = mkSku();
      await page().getByTestId("variant-new").click();
      const vform = page().getByRole("form", { name: "Yeni varyant" });
      await vform.getByLabel("SKU").fill(sku.toLowerCase()); // küçük harf yazılsa da büyütülür
      await expect(vform.getByLabel("SKU")).toHaveValue(sku);
      await vform.getByLabel("Varyant adı").fill("128 GB Siyah");
      await vform.getByLabel("Fiyat (TL)").fill("1.299,90");
      await vform.getByLabel("Seçenekler (isteğe bağlı)").fill("Renk: Siyah");
      await vform.getByRole("button", { name: "Varyant oluştur" }).click();
      await expect(page().getByText(`${sku} oluşturuldu.`)).toBeVisible();
      const vrow = page().getByRole("table", { name: "Varyantlar" }).getByRole("row").filter({ hasText: sku });
      await expect(vrow).toContainText(formatPriceMinor(129_990));
      await expect(vrow).toContainText(/renk: siyah/i); // sunucu seçenek anahtar/değerlerini küçük harfe normalleştirir
      const variantId = (await aGet<ProductDetail>(admin.request, `/products/${productId}`)).variants[0].id;
      expect((await aGet<ProductDetail>(admin.request, `/products/${productId}`)).variants[0].priceMinor).toBe(129_990);

      // sku_taken (aynı SKU, başka seçenek) ve variant_options_taken (başka SKU, aynı seçenek)
      await page().getByTestId("variant-new").click();
      const v2 = page().getByRole("form", { name: "Yeni varyant" });
      await v2.getByLabel("SKU").fill(sku);
      await v2.getByLabel("Varyant adı").fill("Başka");
      await v2.getByLabel("Fiyat (TL)").fill("10");
      await v2.getByLabel("Seçenekler (isteğe bağlı)").fill("Renk: Beyaz");
      await v2.getByRole("button", { name: "Varyant oluştur" }).click();
      await expect(v2.locator("#v-sku-error")).toHaveText("Bu SKU kullanılıyor.");
      await v2.getByLabel("SKU").fill(mkSku());
      await v2.getByLabel("Seçenekler (isteğe bağlı)").fill("Renk: Siyah");
      await v2.getByRole("button", { name: "Varyant oluştur" }).click();
      await expect(v2.getByRole("alert").filter({ hasText: "aynı seçeneklere sahip" })).toBeVisible();
      await expect(v2.getByRole("button", { name: "Listeyi yenile" })).toBeVisible();
      await v2.getByRole("button", { name: "Vazgeç" }).click();
      expect((await aGet<ProductDetail>(admin.request, `/products/${productId}`)).variants).toHaveLength(1);

      // Fiyat doğrulaması arayüzde: 0, negatif, harf, 3 ondalık
      await page().getByRole("button", { name: `Düzenle: ${sku}` }).click();
      const edit = page().getByRole("form", { name: `Varyantı düzenle: ${sku}` });
      for (const bad of ["0", "-5", "abc", "12,345", ""]) {
        await edit.getByLabel("Fiyat (TL)").fill(bad);
        await edit.getByRole("button", { name: "Varyantı kaydet" }).click();
        await expect(edit.locator("#v-fiyat-error"), `fiyat "${bad}"`).toBeVisible();
      }
      await edit.getByRole("button", { name: "Vazgeç" }).click();

      // Stok gir ve ürünü satışa aç (aktif varyant + aktif kategori + stok)
      await receiveApi(admin, await warehouseId(admin.request), variantId, 5);
      await page().getByLabel("Satışta (aktif)").first().check();
      await page().getByRole("button", { name: "Ürünü kaydet" }).click();
      await expect(page().getByText("Ürün bilgisi kaydedildi.")).toBeVisible();

      // VİTRİN: ad ve fiyat, kuruş → TL biçimi doğru
      const shop = await browser.newContext({ baseURL: admin.origin });
      const shopPage = await shop.newPage();
      try {
        await shopPage.goto(`/urun/${slug}`);
        await expect(shopPage.getByRole("heading", { level: 1, name })).toBeVisible();
        await expect(shopPage.locator("main")).toContainText(formatPriceMinor(129_990));
        // Fiyatı yönetimden değiştir: uyarı görünür, kaydedince vitrin güncellenir
        await page().getByRole("button", { name: `Düzenle: ${sku}` }).click();
        const priceForm = page().getByRole("form", { name: `Varyantı düzenle: ${sku}` });
        await priceForm.getByLabel("Fiyat (TL)").fill("999,50");
        await expect(priceForm.getByText("Fiyat değişikliği yeni sipariş önizlemelerini etkiler")).toBeVisible();
        await priceForm.getByRole("button", { name: "Varyantı kaydet" }).click();
        await expect(page().getByText(`${sku} kaydedildi.`)).toBeVisible();
        await expect(page().getByRole("table", { name: "Varyantlar" })).toContainText(formatPriceMinor(99_950));
        await shopPage.reload();
        await expect(shopPage.locator("main")).toContainText(formatPriceMinor(99_950));
        await expect(shopPage.locator("main")).not.toContainText(formatPriceMinor(129_990));
        const publicDetail = await (await shop.request.get(`${api}/products/${slug}`)).json() as { variants: { priceMinor: number }[] };
        expect(publicDetail.variants[0].priceMinor).toBe(99_950);

        // Pasife al: vitrinde 404
        await page().getByLabel("Satışta (aktif)").first().uncheck();
        await page().getByRole("button", { name: "Ürünü kaydet" }).click();
        await expect(page().getByText("Ürün bilgisi kaydedildi.")).toBeVisible();
        const gone = await shopPage.goto(`/urun/${slug}`);
        expect(gone?.status()).toBe(404);
        expect((await shop.request.get(`${api}/products/${slug}`)).status()).toBe(404);
      } finally { await shop.close(); }

      // Denetim izi: ürün + varyant yazmaları, yasak anahtar yok
      const productAudit = (await auditOf("product", productId)).items.map((r) => r.action);
      expect(productAudit).toEqual(expect.arrayContaining(["product.create", "product.update"]));
      const variantAudit = await auditOf("variant", variantId);
      expect(variantAudit.items.map((r) => r.action)).toEqual(expect.arrayContaining(["variant.create", "variant.update"]));
      for (const row of [...variantAudit.items]) for (const key of FORBIDDEN_AUDIT_KEYS) expect(row.changes).not.toHaveProperty(key);
    } finally { if (productId) await retire(admin, { product: { id: productId }, category }); else await retire(admin, { product: { id: randomUUID() }, category }); }
  });

  test("ürün listesi: arama, durum ve kategori süzgeci; sayfalama; özet alanları", async () => {
    const category = await mkCategory(admin);
    const tag = uid();
    const a = await mkProduct(admin, category.id, { name: `Listelik ${tag} A`, isActive: true });
    const b = await mkProduct(admin, category.id, { name: `Listelik ${tag} B` });
    await mkVariant(admin, a.id, { priceMinor: 25_000 });
    await mkVariant(admin, a.id, { priceMinor: 12_500, displayName: "Küçük", options: { boyut: "kucuk" } });
    try {
      await open("/admin/urunler");
      await page().getByLabel("Ara").fill(`Listelik ${tag}`);
      await page().getByRole("button", { name: "Süz" }).click();
      const table = page().getByRole("table", { name: "Ürünler" });
      await expect(table.getByRole("row").filter({ hasText: `Listelik ${tag} A` })).toContainText(formatPriceMinor(12_500)); // en düşük fiyat
      await expect(table.getByRole("row").filter({ hasText: `Listelik ${tag} A` })).toContainText("2/2");
      await expect(table.getByRole("row").filter({ hasText: `Listelik ${tag} B` })).toContainText("Pasif");
      await page().getByLabel("Durum").selectOption("inactive");
      await page().getByRole("button", { name: "Süz" }).click();
      await expect(table.getByRole("row").filter({ hasText: `Listelik ${tag} A` })).toHaveCount(0);
      await expect(table.getByRole("row").filter({ hasText: `Listelik ${tag} B` })).toHaveCount(1);
      await page().getByLabel("Ara").fill(`olmayan-${tag}`);
      await page().getByRole("button", { name: "Süz" }).click();
      await expect(page().getByText("Ölçüte uyan ürün yok")).toBeVisible();
      // API sayfa boyutu üst sınırı
      const list = await aGet<Paged<ProductSummary>>(admin.request, `/products?q=Listelik ${tag}&pageSize=50`);
      expect(list.totalCount).toBe(2);
      expect((await admin.request.get(`${api}/admin/products?pageSize=51`)).status()).toBe(400);
    } finally { await retire(admin, { product: a, category }, { product: b, category }); }
  });

  test("ürün sürüm çakışması arayüzde; varyant çakışması; aynı anda iki PATCH; gövdesiz sürüm 400", async () => {
    const category = await mkCategory(admin);
    const product = await mkProduct(admin, category.id);
    const variant = await mkVariant(admin, product.id);
    try {
      await open(`/admin/urunler/${product.id}`);
      // ürün bilgisi çakışması
      await aWrite(admin, "PATCH", `/products/${product.id}`, { version: product.version, brand: "Başkası" }, 200);
      await page().getByLabel("Marka").fill("Benim");
      await page().getByRole("button", { name: "Ürünü kaydet" }).click();
      await expect(page().getByText("Bu kayıt sizden sonra değişti")).toBeVisible();
      expect((await aGet<ProductDetail>(admin.request, `/products/${product.id}`)).brand).toBe("Başkası");
      await page().getByRole("button", { name: "Güncel kaydı yükle" }).click();
      await expect(page().getByLabel("Marka")).toHaveValue("Başkası");
      await expect(page().getByText("Sürüm 2")).toBeVisible();

      // varyant çakışması
      await page().getByRole("button", { name: `Düzenle: ${variant.sku}` }).click();
      const edit = page().getByRole("form", { name: `Varyantı düzenle: ${variant.sku}` });
      await aWrite(admin, "PATCH", `/variants/${variant.id}`, { version: variant.version, displayName: "Başkasının adı" }, 200);
      await edit.getByLabel("Fiyat (TL)").fill("500");
      await edit.getByRole("button", { name: "Varyantı kaydet" }).click();
      await expect(page().getByText("Bu kayıt sizden sonra değişti")).toBeVisible();
      const now = (await aGet<ProductDetail>(admin.request, `/products/${product.id}`)).variants[0];
      expect(now).toMatchObject({ displayName: "Başkasının adı", priceMinor: variant.priceMinor });

      // paralel: aynı sürümle iki fiyat güncellemesi
      const results = await Promise.all([111_100, 222_200].map((priceMinor) => send(admin.request, "PATCH", `/admin/variants/${variant.id}`, admin.origin, { version: now.version, priceMinor })));
      expect(results.map((r) => r.status()).sort()).toEqual([200, 409]);
      // sürüm şart: eksik / metin / 0 → 400
      for (const version of [undefined, "2", 0]) await aProblem(admin, "PATCH", `/variants/${variant.id}`, { ...(version === undefined ? {} : { version }), priceMinor: 100 }, 400, "validation_failed");
      // SKU ve seçenekler değişmez: gövdedeki sku yok sayılır
      const kept = await aWrite<{ sku: string; version: number }>(admin, "PATCH", `/variants/${variant.id}`, { version: (await aGet<ProductDetail>(admin.request, `/products/${product.id}`)).variants[0].version, sku: "E2E-DEGISTI", displayName: "Yeni ad" }, 200);
      expect(kept.sku).toBe(variant.sku);
    } finally { await retire(admin, { product, category }); }
  });

  test("fiyat değişince eski quote reddedilir (order_quote_changed), verilmiş siparişin birim fiyatı değişmez, vitrin yeni fiyatı gösterir", async ({ browser }) => {
    const s = await mkSellable(admin, { priceMinor: 40_000, stock: 5 });
    const customer = await actor(browser, admin.origin, null);
    try {
      await cartApi(customer.request, admin.origin, s.variant.id, 2);
      const address = await (await send(customer.request, "POST", "/addresses", admin.origin, goodAddress)).json() as { id: string };
      const preview = await (await send(customer.request, "POST", "/checkout/preview", admin.origin, { addressId: address.id })).json() as { quote: string };
      await aWrite(admin, "PATCH", `/variants/${s.variant.id}`, { version: s.variant.version, priceMinor: 45_000 }, 200);
      const stale = await orderSend(customer.request, admin.origin, "/orders", { addressId: address.id, quote: preview.quote }, `key-${randomUUID()}`);
      expect(stale.status()).toBe(409);
      expect((await stale.json()).code).toBe("order_quote_changed");
      // Yeniden önizleme yeni fiyatla; sipariş yeni birim fiyatla oluşur
      const fresh = await (await send(customer.request, "POST", "/checkout/preview", admin.origin, { addressId: address.id })).json() as { quote: string };
      const placed = await orderSend(customer.request, admin.origin, "/orders", { addressId: address.id, quote: fresh.quote }, `key-${randomUUID()}`);
      expect(placed.status()).toBe(201);
      const order = await placed.json() as { id: string; items: { unitPriceMinor: number }[]; subtotalMinor: number };
      expect(order.items[0].unitPriceMinor).toBe(45_000);
      expect(order.subtotalMinor).toBe(90_000);
      // Sonraki fiyat değişikliği verilmiş siparişi etkilemez
      const v = (await aGet<ProductDetail>(admin.request, `/products/${s.product.id}`)).variants[0];
      await aWrite(admin, "PATCH", `/variants/${v.id}`, { version: v.version, priceMinor: 1_000 }, 200);
      const again = await (await customer.request.get(`${api}/orders/${order.id}`)).json() as { items: { unitPriceMinor: number }[]; subtotalMinor: number };
      expect(again.items[0].unitPriceMinor).toBe(45_000);
      expect(again.subtotalMinor).toBe(90_000);
      await send(customer.request, "POST", `/orders/${order.id}/cancel`, admin.origin, {});
    } finally { await customer.context.close(); await retire(admin, s); }
  });

  test("Admin fiyatı değiştirince müşterinin sepeti ve ödeme özeti yeni fiyatı gösterir (API ve arayüz); eski fiyat uyarı olarak görünür", async ({ browser }) => {
    const s = await mkSellable(admin, { priceMinor: 40_000, stock: 5 });
    const customer = await actor(browser, admin.origin, null);
    try {
      await cartApi(customer.request, admin.origin, s.variant.id, 2);
      const addr = await send(customer.request, "POST", "/addresses", admin.origin, goodAddress);
      expect(addr.status()).toBe(201);
      const addressId = (await addr.json() as { id: string }).id;
      await customer.page.goto("/sepet");
      await expect(customer.page.getByTestId("subtotal")).toHaveText(formatPriceMinor(80_000));
      await expect(customer.page.getByTestId("price-changed")).toHaveCount(0);

      await aWrite(admin, "PATCH", `/variants/${s.variant.id}`, { version: s.variant.version, priceMinor: 45_000 }, 200);

      // API: sepet satırı ve toplam, ödeme önizlemesi
      const cart = await (await customer.request.get(`${api}/cart`)).json() as { subtotalMinor: number; items: { unitPriceMinor: number; lineSubtotalMinor: number }[] };
      expect([cart.items[0].unitPriceMinor, cart.items[0].lineSubtotalMinor, cart.subtotalMinor]).toEqual([45_000, 90_000, 90_000]);
      const preview = await (await send(customer.request, "POST", "/checkout/preview", admin.origin, { addressId })).json() as { subtotalMinor: number };
      expect(preview.subtotalMinor).toBe(90_000);

      // Arayüz: sepet (yeni fiyat + 'önceki fiyat' notu) ve ödeme özeti
      await customer.page.goto("/sepet");
      await expect(customer.page.getByTestId("subtotal")).toHaveText(formatPriceMinor(90_000));
      await expect(customer.page.getByTestId("line-total")).toContainText(formatPriceMinor(90_000));
      await expect(customer.page.getByTestId("price-changed")).toContainText(`önceki fiyat ${formatPriceMinor(40_000)}, güncel fiyat ${formatPriceMinor(45_000)}`);
      await customer.page.goto("/checkout");
      await expect(customer.page.getByTestId("preview-subtotal")).toHaveText(formatPriceMinor(90_000));
    } finally { await customer.context.close(); await retire(admin, s); }
  });

  test("görseller: yalnız onaylı listeden eklenir, tam bir ana görsel; onaysız anahtar 409 image_not_approved; sürüm artar", async () => {
    const category = await mkCategory(admin);
    const product = await mkProduct(admin, category.id);
    try {
      const assets = await aGet<ImageAsset[]>(admin.request, "/catalog/image-assets");
      expect(assets.length).toBeGreaterThan(1);
      await aProblem(admin, "PUT", `/products/${product.id}/images`, { version: product.version, images: [{ storageKey: "/images/urun/yok-boyle-bir-gorsel.webp", altText: "x", isPrimary: true }] }, 409, "image_not_approved");
      await aProblem(admin, "PUT", `/products/${product.id}/images`, { version: product.version, images: [{ storageKey: "https://evil.example/x.webp", altText: "x", isPrimary: true }] }, 409, "image_not_approved").catch(async () => {
        const r = await send(admin.request, "PUT", `/admin/products/${product.id}/images`, admin.origin, { version: product.version, images: [{ storageKey: "https://evil.example/x.webp", altText: "x", isPrimary: true }] });
        expect([400, 409]).toContain(r.status());
      });
      await open(`/admin/urunler/${product.id}`);
      await page().getByRole("button", { name: `Ekle: ${assets[0].storageKey}` }).click();
      await page().getByRole("button", { name: `Ekle: ${assets[1].storageKey}` }).click();
      await page().getByRole("button", { name: "Görselleri kaydet" }).click();
      await expect(page().getByText("Her görsel için açıklama (alt metin) yaz.")).toBeVisible(); // yerel doğrulama
      const list = page().getByRole("list", { name: "Seçili görseller" });
      await list.getByLabel("Alt metin").nth(0).fill("Birinci görsel");
      await list.getByLabel("Alt metin").nth(1).fill("İkinci görsel");
      await list.getByLabel("Ana").nth(1).check();
      await page().getByRole("button", { name: "Görselleri kaydet" }).click();
      await expect(page().getByText("Görseller kaydedildi.")).toBeVisible();
      const saved = await aGet<ProductDetail>(admin.request, `/products/${product.id}`);
      expect(saved.version).toBe(product.version + 1);
      expect(saved.images.map((i) => ({ k: i.storageKey, p: i.isPrimary }))).toEqual([{ k: assets[0].storageKey, p: false }, { k: assets[1].storageKey, p: true }]);
      // Aynı eski sürümle tekrar yazma çakışır
      await aProblem(admin, "PUT", `/products/${product.id}/images`, { version: product.version, images: [] }, 409, "version_conflict");
      // 8'den fazla görsel reddedilir
      const nine = Array.from({ length: 9 }, (_, i) => ({ storageKey: assets[i % assets.length].storageKey, altText: "a", isPrimary: i === 0 }));
      const tooMany = await send(admin.request, "PUT", `/admin/products/${product.id}/images`, admin.origin, { version: saved.version, images: nine });
      expect([400, 409]).toContain(tooMany.status());
    } finally { await retire(admin, { product, category }); }
  });
});
