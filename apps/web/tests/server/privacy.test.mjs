import assert from "node:assert/strict";
import test from "node:test";
import { isPrivateRoute } from "../../server/privacy.mjs";

test("private routes match on the decoded pathname", () => {
  for (const url of ["/sepet", "/sepet?x=1", "/sepet/", "/%73epet", "/%73epet?a=b", "//sepet", "/checkout", "/checkout/x", "/hesabim", "/hesabim/adresler", "/hesabim/adresler?x=1", "/siparisler", "/siparisler?page=2", "/siparisler/0192b5a0-0000-7000-8000-000000000001", "/siparisler/0192b5a0-0000-7000-8000-000000000001?x=1", "/siparisler/", "/%73iparisler", "/Siparisler", "//siparisler", "/giris", "/giris?next=%2Fsepet", "/kayit", "/Sepet", "/%2Fsepet", "/./sepet", "/x/../sepet", "/admin", "/admin/", "/admin/urunler", "/admin/urunler/0192b5a0-0000-7000-8000-000000000001", "/admin/siparisler?status=placed", "/admin?_rsc=abc", "/Admin/stok", "/%61dmin/raporlar", "//admin", "/admin/../admin"]) {
    assert.equal(isPrivateRoute(url), true, url);
  }
});

test("public routes stay cacheable by the framework", () => {
  for (const url of ["/", "/urun/abc", "/kategori/oyun", "/arama?q=sepet", "/status", "/gorsel-kaynaklari", "/sepetim-degil/x", "/siparisler-degil", "/arama?q=siparisler", "/urun/siparisler", "/admin-degil", "/administrator", "/kategori/admin", "/arama?q=admin", "/yetkisiz"]) {
    assert.equal(isPrivateRoute(url), false, url);
  }
});

test("malformed encoding and odd input fall back to private instead of throwing", () => {
  for (const url of ["/%E0%A4%A", "/%", "/%zz/x"]) assert.equal(isPrivateRoute(url), true, url);
  assert.equal(isPrivateRoute(undefined), false);
  assert.doesNotThrow(() => isPrivateRoute("http://[::1"));
});
