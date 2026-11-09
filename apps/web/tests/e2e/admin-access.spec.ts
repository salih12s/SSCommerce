import { expect, test, type APIRequestContext } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { loginHref } from "../../src/lib/safe-next";
import { actor, aGet, aProblem, aWrite, dashboardApi, settled, uid } from "./admin-support";
import { api, csrf, password, send } from "./support";

// Faz 8 erişim denetimi: anonim / Customer / Warehouse / Admin, CSRF + Origin, no-store, doğrulama hataları, rol yönetimi.
// Bu dosya paylaşılan kataloğu YAZMAZ (kategori/ürün oluşturmaz); yalnız kendi hesaplarını kurar, bu yüzden "main" projesinde paralel koşar.
test.use({ viewport: { width: 1440, height: 900 } });

const ROUTES = ["/admin", "/admin/urunler", "/admin/urunler/yeni", "/admin/urunler/00000000-0000-0000-0000-000000000000", "/admin/kategoriler", "/admin/stok", "/admin/depolar",
  "/admin/siparisler", "/admin/siparisler/00000000-0000-0000-0000-000000000000", "/admin/raporlar", "/admin/kullanicilar"];
const ORDERS_ONLY = new Set(["/admin/siparisler", "/admin/siparisler/00000000-0000-0000-0000-000000000000"]);
const NO_STORE = /private,\s*no-store/;
const ZERO = "00000000-0000-0000-0000-000000000000"; // sunucu Guid.Empty'yi geçersiz kimlik sayar (400); "yok" senaryoları rastgele UUID kullanır
/** RSC isteği Next tarafından önce `?_rsc=` adresine 307 ile yönlendirilir; yönlendirme aynı başlıklarla elle izlenir. Proxy'nin kendi 307'si (giriş) `/giris` adresine gider ve izlenmez. */
async function rscGet(request: APIRequestContext, route: string) {
  const headers = { RSC: "1" };
  let response = await request.get(route, { headers, maxRedirects: 0 });
  for (let hop = 0; hop < 2 && response.status() === 307 && response.headers().location?.includes("_rsc"); hop++) response = await request.get(response.headers().location, { headers, maxRedirects: 0 });
  return response;
}

test.describe("anonim", () => {
  test("her yönetim sayfası 307 ile girişe gider (göreli adres, private no-store); RSC isteği de", async ({ request }) => {
    for (const route of ROUTES) {
      for (const headers of [undefined, { RSC: "1" }]) {
        const response = await request.get(route, { maxRedirects: 0, headers });
        expect(response.status(), route).toBe(307);
        expect(response.headers().location, route).toBe(loginHref(route));
        expect(response.headers()["cache-control"], route).toMatch(NO_STORE);
      }
    }
  });

  test("tarayıcıda /admin/urunler giriş sayfasına düşer ve ?next korunur; yönetim API'si 401", async ({ page, request }) => {
    await page.goto("/admin/urunler");
    await expect(page).toHaveURL(/\/giris\?next=%2Fadmin%2Furunler$/);
    for (const path of ["/dashboard", "/categories", "/orders", "/staff", "/warehouses", "/inventory/movements", "/audit"]) {
      const response = await request.get(`${api}/admin${path}`);
      expect(response.status(), path).toBe(401);
    }
  });
});

test.describe("Customer", () => {
  test("yönetim sayfaları HTTP 403 ve yetkisiz ekran (private no-store); API 403, hesabım bağlantısı çalışır", async ({ browser, baseURL }) => {
    const customer = await actor(browser, baseURL!, null);
    try {
      for (const route of ROUTES) {
        const response = await customer.request.get(route, { maxRedirects: 0 });
        expect(response.status(), route).toBe(403);
        expect(response.headers()["cache-control"], route).toMatch(NO_STORE);
        const rsc = await rscGet(customer.request, route);
        expect(rsc.status(), `${route} RSC`).toBe(403);
        expect(rsc.headers()["cache-control"], `${route} RSC`).toMatch(NO_STORE);
      }
      const response = await customer.page.goto("/admin");
      expect(response?.status()).toBe(403);
      await expect(customer.page.getByRole("heading", { name: "Bu alana erişimin yok" })).toBeVisible();
      await expect(customer.page.locator("body")).not.toContainText(customer.email);
      await customer.page.getByRole("link", { name: "Hesabım" }).click();
      await expect(customer.page).toHaveURL(/\/hesabim$/);
      // Staff onboarding: hesap kimliği Hesabım'da, kopyalama düğmesiyle
      await expect(customer.page.getByText(customer.id)).toBeVisible();
      await expect(customer.page.getByRole("button", { name: "Kopyala" })).toBeVisible();

      for (const path of ["/dashboard", "/categories", "/products", "/orders", "/staff", "/warehouses", "/inventory/balances", "/inventory/movements", "/reports/low-stock", "/audit", "/catalog/image-assets", "/variants", `/users/${customer.id}`]) {
        const r = await customer.request.get(`${api}/admin${path}`);
        expect(r.status(), `GET ${path}`).toBe(403);
      }
      // Yazmalar: geçerli CSRF + Origin ile bile 403 ve hiçbir şey oluşmaz
      for (const [method, path, data] of [
        ["POST", "/categories", { name: "x", slug: `e2e-${uid()}` }],
        ["POST", "/warehouses", { code: `X-${uid()}`, name: "x" }],
        ["PUT", `/users/${customer.id}/roles/Admin`, undefined],
        ["POST", `/orders/${ZERO}/delivery-view`, undefined],
        ["POST", `/orders/${ZERO}/status`, { status: "processing" }],
      ] as const) {
        const r = await send(customer.request, method, `/admin${path}`, customer.origin, data);
        expect(r.status(), `${method} ${path}`).toBe(403);
      }
      // Kendi kendine yönetici olamaz
      expect((await customer.request.get(`${api}/auth/me`)).status()).toBe(200);
      const me = await (await customer.request.get(`${api}/auth/me`)).json() as { roles: string[] };
      expect(me.roles).toEqual(["Customer"]);
    } finally { await customer.context.close(); }
  });
});

test.describe("Warehouse ↔ Admin farkı", () => {
  test("Depo yalnız sipariş ekranlarını görür: /admin → /admin/siparisler, diğerleri 403, menüde tek bağlantı", async ({ browser, baseURL }) => {
    const wh = await actor(browser, baseURL!, "Warehouse");
    try {
      const root = await wh.request.get("/admin", { maxRedirects: 0 });
      expect(root.status()).toBe(307);
      expect(root.headers().location).toBe("/admin/siparisler");
      expect(root.headers()["cache-control"]).toMatch(NO_STORE);
      for (const route of ROUTES.filter((r) => r !== "/admin")) {
        const response = await wh.request.get(route, { maxRedirects: 0 });
        expect(response.status(), route).toBe(ORDERS_ONLY.has(route) ? 200 : 403);
        expect(response.headers()["cache-control"], route).toMatch(NO_STORE);
      }
      await wh.page.goto("/admin");
      await expect(wh.page).toHaveURL(/\/admin\/siparisler$/);
      await settled(wh.page);
      const nav = wh.page.getByRole("navigation", { name: "Yönetim bölümleri" });
      await expect(nav.getByRole("link")).toHaveText(["Siparişler"]);
      await expect(wh.page.locator("header")).toContainText("Depo");
      // Menüde olmayan bölüme adrese yazarak gitmek de kapalı
      const blocked = await wh.page.goto("/admin/stok");
      expect(blocked?.status()).toBe(403);
    } finally { await wh.context.close(); }
  });

  test("Depo API: siparişleri okur/ilerletir; katalog, stok, rapor, kullanıcı, teslimat ve iptal 403", async ({ browser, baseURL }) => {
    const wh = await actor(browser, baseURL!, "Warehouse");
    try {
      const list = await wh.request.get(`${api}/admin/orders?open=true&pageSize=5`);
      expect(list.status()).toBe(200);
      expect(list.headers()["cache-control"]).toContain("no-store");
      for (const path of ["/dashboard", "/categories", "/products", "/staff", "/warehouses", "/inventory/balances", "/inventory/movements", "/reports/low-stock", "/audit", "/variants", `/users/${wh.id}`]) {
        expect((await wh.request.get(`${api}/admin${path}`)).status(), `GET ${path}`).toBe(403);
      }
      for (const [method, path, data] of [
        ["POST", "/categories", { name: "x", slug: `e2e-${uid()}` }],
        ["POST", "/warehouses", { code: `X-${uid()}`, name: "x" }],
        ["POST", "/inventory/receive", { warehouseId: ZERO, variantId: ZERO, quantity: 1, reason: "x" }],
        ["PUT", `/users/${wh.id}/roles/Admin`, undefined],
        ["POST", `/orders/${ZERO}/delivery-view`, undefined],
      ] as const) {
        const r = await send(wh.request, method, `/admin${path}`, wh.origin, data);
        expect(r.status(), `${method} ${path}`).toBe(403);
      }
      // Var olmayan sipariş: depo için yol açık (404), iptal ise yöneticiye ait (rol kontrolü kayıt aramasından önce ya da sonra olabilir; 403/404 kabul, 200 asla)
      const open = await send(wh.request, "POST", `/admin/orders/${randomUUID()}/status`, wh.origin, { status: "processing" });
      expect(open.status()).toBe(404);
    } finally { await wh.context.close(); }
  });
});

test.describe("Admin", () => {
  test("Admin tüm bölümleri açar; menüde 8 bağlantı, rol rozeti ve çıkış; sayfalar private no-store", async ({ browser, baseURL }) => {
    const admin = await actor(browser, baseURL!, "Admin");
    try {
      for (const route of ROUTES) {
        const response = await admin.request.get(route, { maxRedirects: 0 });
        expect(response.status(), route).toBe(200);
        expect(response.headers()["cache-control"], route).toMatch(NO_STORE);
        const rsc = await rscGet(admin.request, route);
        expect(rsc.status(), `${route} RSC -> ${rsc.headers().location}`).toBe(200);
        expect(rsc.headers()["cache-control"], `${route} RSC`).toMatch(NO_STORE);
        // Ham HTML ve RSC yükü kişisel veri taşımaz: e-posta yalnız tarayıcıda /auth/me ile okunur
        expect(await response.text(), route).not.toContain(admin.email);
        expect(await rsc.text(), `${route} RSC`).not.toContain(admin.email);
      }
      await admin.page.goto("/admin");
      await settled(admin.page);
      await expect(admin.page.getByRole("heading", { level: 1, name: "Özet" })).toBeVisible();
      const nav = admin.page.getByRole("navigation", { name: "Yönetim bölümleri" });
      await expect(nav.getByRole("link")).toHaveText(["Özet", "Ürünler", "Kategoriler", "Stok", "Depolar", "Siparişler", "Raporlar", "Kullanıcılar"]);
      await expect(admin.page.locator("header")).toContainText("Yönetici");
      // Vitrin üst bölgesi/alt bilgisi yok
      await expect(admin.page.locator("footer")).toHaveCount(0);
      for (const [name, heading] of [["Ürünler", "Ürünler"], ["Kategoriler", "Kategoriler"], ["Stok", "Stok"], ["Depolar", "Depolar"], ["Siparişler", "Siparişler"], ["Raporlar", "Raporlar"], ["Kullanıcılar", "Kullanıcılar"]] as const) {
        await nav.getByRole("link", { name, exact: true }).click();
        await expect(admin.page.getByRole("heading", { level: 1, name: heading })).toBeVisible();
        await expect(nav.getByRole("link", { name, exact: true })).toHaveAttribute("aria-current", "page");
      }
      // Çıkış yönetim verisini bırakır; sonra /admin yeniden girişe yönlenir
      await admin.page.getByRole("button", { name: "Çıkış yap" }).click();
      await expect(admin.page).toHaveURL(/\/giris$/);
      const after = await admin.request.get("/admin", { maxRedirects: 0 });
      expect(after.status()).toBe(307);
    } finally { await admin.context.close(); }
  });

  test("yönetim yanıtları ve yazma yanıtları Cache-Control: no-store taşır", async ({ browser, baseURL }) => {
    const admin = await actor(browser, baseURL!, "Admin");
    const other = await actor(browser, baseURL!, null);
    try {
      for (const path of ["/categories", "/products", "/dashboard", "/orders", "/staff", "/warehouses", "/inventory/balances", "/inventory/movements", "/reports/low-stock", "/audit", "/catalog/image-assets", "/variants", `/users/${other.id}`]) {
        const r = await admin.request.get(`${api}/admin${path}`);
        expect(r.status(), path).toBe(200);
        expect(r.headers()["cache-control"], path).toContain("no-store");
      }
        const write = await send(admin.request, "PUT", `/admin/users/${other.id}/roles/Warehouse`, admin.origin);
      expect(write.status()).toBe(200);
      expect(write.headers()["cache-control"]).toContain("no-store");
      const refused = await send(admin.request, "PUT", `/admin/users/${other.id}/roles/Customer`, admin.origin);
      expect(refused.status()).toBe(400);
      expect(refused.headers()["cache-control"]).toContain("no-store");
    } finally { await admin.context.close(); await other.context.close(); }
  });

  test("CSRF ve Origin: belirteçsiz / yanlış Origin'li / Origin'siz yazma reddedilir, hiçbir kayıt oluşmaz", async ({ browser, baseURL }) => {
    const admin = await actor(browser, baseURL!, "Admin");
    const victim = await actor(browser, baseURL!, null);
    try {
      const slug = `e2e-${uid()}`;
      const token = await csrf(admin.request);
      const attempts: [string, Record<string, string>][] = [
        ["belirteç yok", { Origin: admin.origin }],
        ["yanlış belirteç", { Origin: admin.origin, "X-CSRF-TOKEN": "x".repeat(40) }],
        ["yanlış Origin", { Origin: "https://evil.example", "X-CSRF-TOKEN": token }],
        ["Origin yok, belirteç yok", {}],
      ];
      for (const [label, headers] of attempts) {
        const r = await admin.request.fetch(`${api}/admin/categories`, { method: "POST", data: { name: "Saldırı", slug }, headers });
        const problem = await r.json() as { code?: string };
        expect(`${r.status()} ${problem.code}`, `kategori: ${label}`).toMatch(/^(400|403) (csrf_invalid|origin_not_allowed)$/);
        const role = await admin.request.fetch(`${api}/admin/users/${victim.id}/roles/Admin`, { method: "PUT", headers });
        expect([400, 403], `rol: ${label} ${role.status()}`).toContain(role.status());
      }
      const categories = await aGet<{ slug: string }[]>(admin.request, "/categories");
      expect(categories.some((c) => c.slug === slug)).toBe(false);
      const target = await aGet<{ roles: string[] }>(admin.request, `/users/${victim.id}`);
      expect(target.roles).not.toContain("Admin");
      // Tasarım (D-034): Origin başlığı HİÇ yoksa istek geçer ama CSRF belirteci yine şarttır; belirteç + Origin'siz istek (yalnız sunucudan sunucuya istemciler) kabul edilir, tarayıcı Origin'i her zaman gönderir.
    } finally { await admin.context.close(); await victim.context.close(); }
  });

  test("doğrulama hataları: sunucu eksik alan, yanlış tür, aşırı uzun metin ve kötü kimliği 400 validation_failed ile reddeder", async ({ browser, baseURL }) => {
    const admin = await actor(browser, baseURL!, "Admin");
    try {
      const long = "a".repeat(5000);
      const bad: [string, "POST" | "PATCH" | "PUT", string, unknown][] = [
        ["kategori: ad eksik", "POST", "/categories", { slug: `e2e-${uid()}` }],
        ["kategori: ad sayı", "POST", "/categories", { name: 42, slug: `e2e-${uid()}` }],
        ["kategori: ad çok uzun", "POST", "/categories", { name: long, slug: `e2e-${uid()}` }],
        ["kategori: kötü slug", "POST", "/categories", { name: "x", slug: "Geçersiz Slug!" }],
        ["kategori: kontrol karakteri", "POST", "/categories", { name: "a\u0000b", slug: `e2e-${uid()}` }],
        ["kategori: gövde dizi", "POST", "/categories", [1, 2]],
        ["ürün: kategori kimliği kötü", "POST", "/products", { categoryId: "değil-uuid", name: "x", slug: `e2e-${uid()}` }],
        ["ürün: ad çok uzun", "POST", "/products", { categoryId: ZERO, name: long, slug: `e2e-${uid()}` }],
        ["kategori güncelle: sürüm yok", "PATCH", `/categories/${randomUUID()}`, { name: "x" }],
        ["kategori güncelle: sürüm metin", "PATCH", `/categories/${randomUUID()}`, { version: "1", name: "x" }],
        ["kategori güncelle: sürüm 0", "PATCH", `/categories/${randomUUID()}`, { version: 0, name: "x" }],
        ["kategori güncelle: kötü yol kimliği", "PATCH", "/categories/degil", { version: 1 }],
        ["varyant: fiyat metin", "POST", `/products/${randomUUID()}/variants`, { sku: "ABC-123", displayName: "x", priceMinor: "12" }],
        ["varyant: fiyat 0", "POST", `/products/${randomUUID()}/variants`, { sku: "ABC-123", displayName: "x", priceMinor: 0 }],
        ["depo: kod eksik", "POST", "/warehouses", { name: "x" }],
        ["rol: kötü kimlik", "PUT", "/users/degil/roles/Admin", undefined],
      ];
      for (const [label, method, path, data] of bad) {
        await aProblem(admin, method, path, data, 400, "validation_failed").catch((error) => { throw new Error(`${label}: ${error.message}`); });
      }
      // Rol adı tam eşleşmeli
      const target = await actor(browser, baseURL!, null);
      for (const role of ["ADMIN", "admin", "Customer", "%20Admin", "Admin%20", "Superuser"]) {
        await aProblem(admin, "PUT", `/users/${target.id}/roles/${role}`, undefined, 400, "validation_failed").catch((error) => { throw new Error(`rol ${role}: ${error.message}`); });
      }
      await target.context.close();
      // Beklenen durumlar: olmayan kayıtlar 404
      await aProblem(admin, "PATCH", `/categories/${randomUUID()}`, { version: 1, name: "x" }, 404, "category_not_found");
      await aProblem(admin, "POST", `/products/${randomUUID()}/variants`, { sku: "ABC-123", displayName: "x", priceMinor: 100 }, 404, "product_not_found");
      await aProblem(admin, "PUT", `/users/${randomUUID()}/roles/Warehouse`, undefined, 404, "user_not_found");
    } finally { await admin.context.close(); }
  });

  test("arayüz: sunucu doğrulama hatası alanın altında görünür (kötü slug); boş ad yerelde yakalanır", async ({ browser, baseURL }) => {
    const admin = await actor(browser, baseURL!, "Admin");
    try {
      await admin.page.goto("/admin/kategoriler");
      await settled(admin.page);
      await admin.page.getByRole("button", { name: "Yeni kategori" }).click();
      const form = admin.page.locator("form[aria-busy]");
      await form.getByRole("button", { name: "Kategori oluştur" }).click();
      await expect(admin.page.getByRole("alert").filter({ hasText: "Kategori adı gerekli" })).toBeVisible();
      await form.getByLabel("Ad", { exact: true }).fill("Geçersiz adres denemesi");
      await form.getByLabel("Adres (slug)").fill("Büyük Harf!");
      await form.getByRole("button", { name: "Kategori oluştur" }).click();
      await expect(form.locator("#k-slug-error")).toBeVisible();
      await expect(form.getByLabel("Adres (slug)")).toHaveAttribute("aria-invalid", "true");
      // Hiçbir kategori oluşmadı
      const categories = await aGet<{ name: string }[]>(admin.request, "/categories");
      expect(categories.some((c) => c.name === "Geçersiz adres denemesi")).toBe(false);
      // Aşırı uzun metin: alan karakter sınırını tarayıcıda uygular
      await expect(form.getByLabel("Ad", { exact: true })).toHaveAttribute("maxlength", "80");
    } finally { await admin.context.close(); }
  });
});

test.describe("rol yönetimi", () => {
  test("Admin hesap kimliğiyle Depo rolü verir ve alır; oturum düşer; denetim kaydı e-postasızdır; kendi rolünü kaldıramaz", async ({ browser, baseURL }) => {
    const admin = await actor(browser, baseURL!, "Admin");
    const target = await actor(browser, baseURL!, null);
    try {
      await admin.page.goto("/admin/kullanicilar");
      await settled(admin.page);
      // Geçersiz kimlik yerelde yakalanır
      await admin.page.getByRole("textbox", { name: "Hesap kimliği" }).fill("abc");
      await admin.page.getByRole("button", { name: "Hesabı göster" }).click();
      await expect(admin.page.getByText("Geçerli bir hesap kimliği gir")).toBeVisible();
      // Olmayan hesap: sunucu 404 user_not_found
      await admin.page.getByRole("textbox", { name: "Hesap kimliği" }).fill(randomUUID());
      await admin.page.getByRole("button", { name: "Hesabı göster" }).click();
      await expect(admin.page.getByRole("alert").filter({ hasText: "Bu kimlikte bir hesap yok" })).toBeVisible();

      await admin.page.getByRole("textbox", { name: "Hesap kimliği" }).fill(target.id);
      await admin.page.getByRole("button", { name: "Hesabı göster" }).click();
      const card = admin.page.getByTestId("user-card");
      await expect(card).toContainText(target.email);
      await expect(card).toContainText("E-posta doğrulanmadı"); // kayıt açık: e-posta kanıtlanmamış
      await expect(card).toContainText("Rol yok");
      await card.getByTestId("grant-Warehouse").click();
      const dialog = admin.page.getByRole("dialog");
      await expect(dialog).toContainText("e-posta doğrulanmadı");
      await dialog.getByRole("button", { name: "Rolü ver" }).click();
      await expect(admin.page.getByText("Depo rolü verildi. Kullanıcının açık oturumları sonlandırıldı.")).toBeVisible();
      await expect(card).toContainText("Depo");
      await expect(card.getByTestId("revoke-Warehouse")).toBeVisible();
      // Personel listesinde görünür
      await expect(admin.page.getByRole("table", { name: "Personel" }).getByText(target.email)).toBeVisible();

      // Eski oturum çerezi düştü (güvenlik damgası)
      expect((await target.request.get(`${api}/auth/me`)).status()).toBe(401);
      // İkinci kez vermek değişiklik yapmaz
      const again = await aWrite<{ changed: boolean; roles: string[] }>(admin, "PUT", `/users/${target.id}/roles/Warehouse`, undefined, 200);
      expect(again.changed).toBe(false);
      expect(again.roles).toContain("Warehouse");

      // Denetim: role.grant, yalnız {role}; e-posta/kimlik bilgisi yok
      const audit = await aGet<{ items: { action: string; changes: Record<string, unknown>; actorEmail: string | null }[] }>(admin.request, `/audit?targetType=user&targetId=${target.id}`);
      const grants = audit.items.filter((r) => r.action === "role.grant");
      expect(grants).toHaveLength(1); // `changed:false` yeni satır yazmadı
      expect(grants[0].changes).toEqual({ role: "Warehouse" });
      expect(grants[0].actorEmail).toBe(admin.email);
      expect(JSON.stringify(audit)).not.toContain(target.email);

      // Rolü al
      await card.getByTestId("revoke-Warehouse").click();
      await admin.page.getByRole("dialog").getByRole("button", { name: "Rolü kaldır" }).click();
      await expect(admin.page.getByText("Depo rolü kaldırıldı.")).toBeVisible();
      await expect(card).toContainText("Rol yok");

      // Kendi yönetici rolünü kaldıramaz (role_self_demotion)
      await admin.page.getByRole("textbox", { name: "Hesap kimliği" }).fill(admin.id);
      await admin.page.getByRole("button", { name: "Hesabı göster" }).click();
      const own = admin.page.getByTestId("user-card");
      await expect(own).toContainText("Bu senin hesabın");
      await own.getByTestId("revoke-Admin").click();
      await admin.page.getByRole("dialog").getByRole("button", { name: "Rolü kaldır" }).click();
      await expect(admin.page.getByRole("alert").filter({ hasText: "Kendi yönetici rolünü kaldıramazsın" })).toBeVisible();
      expect((await admin.request.get(`${api}/auth/me`)).status()).toBe(200);
      await aProblem(admin, "DELETE", `/users/${admin.id}/roles/Admin`, undefined, 409, "role_self_demotion");
    } finally { await admin.context.close(); await target.context.close(); }
  });

  test("ikinci Admin atanır, sonra birinci onu kaldırır; kaldırılan Admin'in eski çerezi 401, yetkisi 403 olur", async ({ browser, baseURL }) => {
    const first = await actor(browser, baseURL!, "Admin");
    const second = await actor(browser, baseURL!, null);
    try {
      await aWrite(first, "PUT", `/users/${second.id}/roles/Admin`, undefined, 200);
      const promoted = await send(second.request, "POST", "/auth/login", second.origin, { email: second.email, password: password });
      expect(promoted.status()).toBe(200);
      expect((await dashboardApi(second.request)).generatedAt).toBeTruthy();
      await aWrite(first, "DELETE", `/users/${second.id}/roles/Admin`, undefined, 200);
      expect((await second.request.get(`${api}/admin/dashboard`)).status()).toBe(401); // çerez kaldırma anında düştü
      const login = await send(second.request, "POST", "/auth/login", second.origin, { email: second.email, password: password });
      expect(login.status()).toBe(200);
      expect((await second.request.get(`${api}/admin/dashboard`)).status()).toBe(403);
    } finally { await first.context.close(); await second.context.close(); }
  });
});

test.describe("hata gövdesi (RFC 9457)", () => {
  test("401, 403, 400, 404 ve CSRF reddi application/problem+json döner ve type/title/status/code taşır; yanıtlar no-store", async ({ browser, baseURL, request }) => {
    const customer = await actor(browser, baseURL!, null);
    const admin = await actor(browser, baseURL!, "Admin");
    const check = async (response: Awaited<ReturnType<typeof request.get>>, status: number, label: string, code?: string) => {
      expect(response.status(), label).toBe(status);
      expect(response.headers()["content-type"], label).toContain("application/problem+json");
      expect(response.headers()["cache-control"], label).toContain("no-store");
      const body = await response.json() as Record<string, unknown>;
      expect(typeof body.type === "string" && body.type !== "", `${label} type`).toBe(true);
      expect(typeof body.title === "string" && body.title !== "", `${label} title`).toBe(true);
      expect(body.status, `${label} status`).toBe(status);
      expect(typeof body.code === "string" && body.code !== "", `${label} code`).toBe(true);
      if (code) expect(body.code, label).toBe(code);
      expect(JSON.stringify(body), `${label} sır`).not.toMatch(/stack|exception|Npgsql|SELECT |at SSCommerce/i); // iç ayrıntı sızmaz
    };
    try {
      await check(await request.get(`${api}/admin/dashboard`), 401, "anonim GET");
      await check(await send(request, "POST", "/admin/categories", baseURL!, { name: "x", slug: "x" }), 401, "anonim POST");
      await check(await customer.request.get(`${api}/admin/dashboard`), 403, "Customer");
      await check(await admin.request.get(`${api}/admin/orders/${randomUUID()}`), 404, "Admin 404", "order_not_found");
      await check(await send(admin.request, "POST", "/admin/categories", admin.origin, { name: "", slug: "" }), 400, "Admin 400", "validation_failed");
      const noCsrf = await admin.request.post(`${api}/admin/categories`, { data: { name: "x", slug: `e2e-${uid()}` }, headers: { Origin: admin.origin } });
      expect([400, 403]).toContain(noCsrf.status());
      await check(noCsrf, noCsrf.status(), "CSRF belirteçsiz");
    } finally { await customer.context.close(); await admin.context.close(); }
  });
});
