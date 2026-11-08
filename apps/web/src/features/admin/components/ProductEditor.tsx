"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useMemo, useState, type FormEvent } from "react";
import { aGet, aSend, enc } from "@/features/admin/client";
import { slugify } from "@/features/admin/format";
import { minorToTlInput, parseTlToMinor } from "@/features/admin/money";
import { listOf, type AdminImage, type AdminVariant, type Category, type ImageAsset, type ProductDetail } from "@/features/admin/types";
import { useRemote } from "@/features/admin/use-remote";
import type { ApiFailure } from "@/lib/api";
import { formatPriceMinor } from "@/lib/format";
import { Thumb } from "@/features/orders/components/OrderParts";
import { btnPrimary } from "@/components/ui/styles";
import { flatten } from "./CategoriesAdmin";
import { ActiveChip, btnSmall, CancelButton, CheckField, Chip, ConflictNotice, DataTable, describedBy, Empty, FailureBox, Field, fieldError, FormActions, inputClass, Kunye, Loaded, Notice, PageHead, Section, SubmitButton, textareaClass, type Column } from "./kit";

const imageSrc = (image: { storageKey: string; url?: string }) => image.url ?? (image.storageKey.startsWith("/") ? image.storageKey : null);

/** "Anahtar: Değer" satırları <-> nesne. İlk ":" ayırıcıdır. Hata varsa metin döner. */
function parsePairs(text: string, label: string, maxCount: number, maxKey: number, maxValue: number): { value: Record<string, string> } | { error: string } {
  const out: Record<string, string> = {};
  for (const [index, raw] of text.split("\n").entries()) {
    const line = raw.trim();
    if (line === "") continue;
    const at = line.indexOf(":");
    if (at < 1) return { error: `${label}: ${index + 1}. satır “Anahtar: Değer” biçiminde olmalı.` };
    const key = line.slice(0, at).trim(), value = line.slice(at + 1).trim();
    if (key === "" || value === "") return { error: `${label}: ${index + 1}. satırda anahtar ve değer dolu olmalı.` };
    if (key.length > maxKey || value.length > maxValue) return { error: `${label}: ${index + 1}. satır çok uzun (anahtar en çok ${maxKey}, değer en çok ${maxValue} karakter).` };
    if (key in out) return { error: `${label}: “${key}” iki kez yazılmış.` };
    out[key] = value;
  }
  if (Object.keys(out).length > maxCount) return { error: `${label}: en çok ${maxCount} satır olabilir.` };
  return { value: out };
}
const pairsToText = (pairs: Record<string, string> | null | undefined) => Object.entries(pairs ?? {}).map(([k, v]) => `${k}: ${v}`).join("\n");

export function ProductEditor({ id }: { id: string | null }) {
  const categories = useRemote(() => aGet<Category[] | { items: Category[] }>("/admin/categories"), "kategoriler");
  const product = useRemote<ProductDetail | null>(() => (id ? aGet<ProductDetail | null>(`/admin/products/${enc(id)}`) : Promise.resolve({ ok: true as const, data: null })), id ?? "yeni");
  return <>
    <p className="mb-2"><Link href="/admin/urunler" className="inline-flex min-h-11 items-center text-sm font-semibold text-accent-deep underline underline-offset-2">Tüm ürünler</Link></p>
    <Loaded remote={categories} label="Kategoriler yükleniyor…">{(rawCategories) => {
      const cats = listOf(rawCategories);
      return <Loaded remote={product} label="Ürün yükleniyor…">{(p) => {
        if (id && !p) return <Empty title="Ürün bulunamadı" />;
        if (cats.length === 0) return <Empty title="Önce bir kategori oluştur">Ürün bir kategoriye bağlıdır. <Link href="/admin/kategoriler" className="font-semibold text-accent-deep underline">Kategoriler</Link> sayfasından ekleyebilirsin.</Empty>;
        return <Editor product={p} categories={cats} reload={product.reload} />;
      }}</Loaded>;
    }}</Loaded>
  </>;
}

function Editor({ product, categories, reload }: { product: ProductDetail | null; categories: Category[]; reload: () => void }) {
  // Görsel kaydı sonrası liste yenilenince Images anahtarı değişip bileşen yeniden kurulur; onay iletisi bu yüzden burada (üstte) tutulur.
  const [imagesNotice, setImagesNotice] = useState("");
  const [infoNotice, setInfoNotice] = useState("");
  return <>
    <PageHead title={product ? product.name : "Yeni ürün"} lead={product ? <span className="inline-flex flex-wrap items-center gap-2"><Kunye>{product.slug}</Kunye><ActiveChip active={product.isActive} />{product.isDemo && <Chip>demo</Chip>}<span className="text-sm">Sürüm {product.version}</span></span> : "Ürün önce pasif oluşturulur; varyant, fiyat ve görsel ekledikten sonra satışa aç."} />
    <Section id="bilgi" title="Ürün bilgisi"><InfoForm key={product ? `${product.id}-${product.version}` : "yeni"} product={product} categories={categories} reload={reload} saved={infoNotice} setSaved={setInfoNotice} /></Section>
    {product && <>
      <Section id="varyantlar" title="Varyantlar" note="SKU ve seçenekler oluşturulduktan sonra değişmez; yanlış varyant pasife alınır."><Variants product={product} reload={reload} /></Section>
      <Section id="gorseller" title="Görseller" note="Yalnız onaylı yerel görseller seçilebilir. En çok 8; tam olarak biri ana görsel olur."><Images key={JSON.stringify(product.images)} product={product} reload={reload} notice={imagesNotice} setNotice={setImagesNotice} /></Section>
    </>}
  </>;
}

function InfoForm({ product, categories, reload, saved, setSaved }: { product: ProductDetail | null; categories: Category[]; reload: () => void; saved: string; setSaved: (message: string) => void }) {
  const router = useRouter();
  const options = useMemo(() => flatten(categories), [categories]);
  const [name, setName] = useState(product?.name ?? "");
  const [slug, setSlug] = useState(product?.slug ?? "");
  const [slugTouched, setSlugTouched] = useState(false);
  const [categoryId, setCategoryId] = useState(product?.categoryId ?? "");
  const [brand, setBrand] = useState(product?.brand ?? "");
  const [description, setDescription] = useState(product?.description ?? "");
  const [specs, setSpecs] = useState(pairsToText(product?.specifications));
  const [isActive, setActive] = useState(product?.isActive ?? false);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiFailure | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});

  async function submit(event: FormEvent) {
    event.preventDefault();
    setFailure(null); setSaved("");
    const e: Record<string, string> = {};
    if (name.trim() === "") e.name = "Ürün adı gerekli.";
    if (!product && slug.trim() === "") e.slug = "Adres (slug) gerekli.";
    if (categoryId === "") e.categoryId = "Kategori seç.";
    if (description.length > 4000) e.description = "Açıklama en çok 4000 karakter olabilir.";
    const parsed = parsePairs(specs, "Özellikler", 50, 60, 300);
    if ("error" in parsed) e.specifications = parsed.error;
    setErrors(e);
    if (Object.keys(e).length > 0 || !("value" in parsed)) return;
    setBusy(true);
    let result;
    if (product) {
      const changes: Record<string, unknown> = {};
      if (name.trim() !== product.name) changes.name = name.trim();
      if (categoryId !== product.categoryId) changes.categoryId = categoryId;
      if ((brand.trim() || null) !== (product.brand || null)) changes.brand = brand.trim() || null;
      if ((description.trim() || null) !== (product.description || null)) changes.description = description.trim() || null;
      if (JSON.stringify(parsed.value) !== JSON.stringify(product.specifications ?? {})) changes.specifications = parsed.value;
      if (isActive !== product.isActive) changes.isActive = isActive;
      if (Object.keys(changes).length === 0) { setBusy(false); setSaved("Değişiklik yok."); return; }
      result = await aSend<ProductDetail>("PATCH", `/admin/products/${enc(product.id)}`, { version: product.version, ...changes });
    } else {
      result = await aSend<ProductDetail>("POST", "/admin/products", {
        categoryId, name: name.trim(), slug: slug.trim(), ...(brand.trim() ? { brand: brand.trim() } : {}), ...(description.trim() ? { description: description.trim() } : {}),
        ...(Object.keys(parsed.value).length > 0 ? { specifications: parsed.value } : {}), isActive,
      });
    }
    setBusy(false);
    if (!result.ok) { setFailure(result); return; }
    if (!product) { router.push(result.data?.id ? `/admin/urunler/${result.data.id}` : "/admin/urunler"); return; }
    setSaved("Ürün bilgisi kaydedildi."); reload();
  }
  const slugErr = errors.slug ?? fieldError(failure, "slug") ?? (failure?.code === "product_slug_taken" ? failure.message : undefined);
  const err = (key: string) => errors[key] ?? fieldError(failure, key);
  const general = failure && !["version_conflict", "product_slug_taken"].includes(failure.code) && Object.keys(failure.errors).length === 0 ? failure : null;
  return <form onSubmit={(ev) => void submit(ev)} noValidate aria-busy={busy} className="max-w-3xl rounded-lg border border-line bg-card p-4 sm:p-5">
    <div className="grid gap-4 sm:grid-cols-2">
      <Field label="Ad" id="u-ad" error={err("name")}>
        <input id="u-ad" value={name} maxLength={160} onChange={(ev) => { setName(ev.target.value); if (!product && !slugTouched) setSlug(slugify(ev.target.value)); }} aria-invalid={!!err("name")} aria-describedby={describedBy("u-ad", false, !!err("name"))} className={inputClass} />
      </Field>
      <Field label="Adres (slug)" id="u-slug" error={slugErr} hint={product ? "Oluşturulduktan sonra değişmez." : "Küçük harf, rakam ve tire."}>
        <input id="u-slug" value={slug} maxLength={160} disabled={!!product} onChange={(ev) => { setSlug(ev.target.value); setSlugTouched(true); }} aria-invalid={!!slugErr} aria-describedby={describedBy("u-slug", true, !!slugErr)} className={`${inputClass} font-mono`} />
      </Field>
      <Field label="Kategori" id="u-kategori" error={err("categoryId")}>
        <select id="u-kategori" value={categoryId} onChange={(ev) => setCategoryId(ev.target.value)} aria-invalid={!!err("categoryId")} className={inputClass}>
          <option value="">Kategori seç</option>
          {options.map((n) => <option key={n.category.id} value={n.category.id}>{`${"— ".repeat(n.depth)}${n.category.name}${n.category.isActive ? "" : " (pasif)"}`}</option>)}
        </select>
      </Field>
      <Field label="Marka" id="u-marka" error={err("brand")}><input id="u-marka" value={brand} maxLength={80} onChange={(ev) => setBrand(ev.target.value)} className={inputClass} /></Field>
    </div>
    <Field label="Açıklama" id="u-aciklama" className="mt-4" error={err("description")} hint={`${description.length}/4000`}>
      <textarea id="u-aciklama" rows={5} maxLength={4000} value={description} onChange={(ev) => setDescription(ev.target.value)} aria-invalid={!!err("description")} aria-describedby={describedBy("u-aciklama", true, !!err("description"))} className={textareaClass} />
    </Field>
    <Field label="Özellikler" id="u-ozellik" className="mt-4" error={err("specifications")} hint="Her satıra bir özellik: “Ekran: 6,1 inç”. En çok 50 satır.">
      <textarea id="u-ozellik" rows={4} value={specs} onChange={(ev) => setSpecs(ev.target.value)} aria-invalid={!!err("specifications")} aria-describedby={describedBy("u-ozellik", true, !!err("specifications"))} className={`${textareaClass} font-mono text-sm`} />
    </Field>
    <div className="mt-3"><CheckField id="u-aktif" label="Satışta (aktif)" checked={isActive} onChange={setActive} hint="Ürün ancak en az bir aktif varyantı, aktif kategorisi ve stoğu varsa vitrinde satın alınabilir." /></div>
    {failure?.code === "version_conflict" && <div className="mt-4"><ConflictNotice onReload={reload} /></div>}
    {general && <div className="mt-4"><FailureBox failure={general} /></div>}
    {saved && <div className="mt-4"><Notice tone="ok">{saved}</Notice></div>}
    <FormActions><SubmitButton busy={busy}>{product ? "Ürünü kaydet" : "Ürünü oluştur"}</SubmitButton>{!product && <CancelButton onClick={() => router.push("/admin/urunler")} disabled={busy} />}</FormActions>
  </form>;
}

type VariantEditing = { kind: "new" } | { kind: "edit"; id: string } | null;

function Variants({ product, reload }: { product: ProductDetail; reload: () => void }) {
  const [editing, setEditing] = useState<VariantEditing>(null);
  const [saved, setSaved] = useState("");
  const current = editing?.kind === "edit" ? product.variants.find((v) => v.id === editing.id) ?? null : null;
  const columns: Column<AdminVariant>[] = [
    { key: "sku", header: "SKU", primary: true, cell: (v) => <Kunye>{v.sku}</Kunye> },
    { key: "n", header: "Ad", cell: (v) => <span className="break-words font-semibold">{v.displayName}</span> },
    { key: "o", header: "Seçenekler", cell: (v) => Object.keys(v.options ?? {}).length === 0 ? <span className="text-fg-muted">—</span> : <span className="break-words">{Object.entries(v.options).map(([k, val]) => `${k}: ${val}`).join(", ")}</span> },
    { key: "p", header: "Fiyat", align: "right", cell: (v) => <span className="font-mono">{formatPriceMinor(v.priceMinor)}</span> },
    { key: "st", header: "Durum", cell: (v) => <ActiveChip active={v.isActive} /> },
    { key: "act", header: "İşlem", cell: (v) => <button aria-label={`Düzenle: ${v.sku}`} type="button" className={btnSmall} onClick={() => { setSaved(""); setEditing({ kind: "edit", id: v.id }); }}>Düzenle</button> },
  ];
  const done = (message: string) => { setSaved(message); setEditing(null); reload(); };
  return <>
    {saved && <div className="mb-3"><Notice tone="ok">{saved}</Notice></div>}
    {product.variants.length === 0 ? <Empty title="Henüz varyant yok">Satış için en az bir varyant (SKU ve fiyat) gerekir.</Empty> : <DataTable caption="Varyantlar" rows={product.variants} columns={columns} rowKey={(v) => v.id} />}
    <div className="mt-4">
      {editing === null && <button type="button" className={btnSmall} onClick={() => { setSaved(""); setEditing({ kind: "new" }); }} data-testid="variant-new">Yeni varyant</button>}
      {editing?.kind === "new" && <VariantForm productId={product.id} variant={null} onDone={done} onCancel={() => setEditing(null)} reload={reload} />}
      {editing?.kind === "edit" && current && <VariantForm key={`${current.id}-${current.version}`} productId={product.id} variant={current} onDone={done} onCancel={() => setEditing(null)} reload={reload} />}
    </div>
  </>;
}

function VariantForm({ productId, variant, onDone, onCancel, reload }: { productId: string; variant: AdminVariant | null; onDone: (m: string) => void; onCancel: () => void; reload: () => void }) {
  const [sku, setSku] = useState(variant?.sku ?? "");
  const [displayName, setDisplayName] = useState(variant?.displayName ?? "");
  const [price, setPrice] = useState(variant ? minorToTlInput(variant.priceMinor) : "");
  const [optionText, setOptionText] = useState(pairsToText(variant?.options));
  const [isActive, setActive] = useState(variant?.isActive ?? true);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiFailure | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  async function submit(event: FormEvent) {
    event.preventDefault();
    setFailure(null);
    const e: Record<string, string> = {};
    if (!variant && !/^[A-Z0-9-]{3,40}$/.test(sku.trim())) e.sku = "SKU 3-40 karakter; büyük harf, rakam ve tire.";
    if (displayName.trim() === "") e.displayName = "Varyant adı gerekli.";
    const minor = parseTlToMinor(price);
    if (minor === null) e.price = "Geçerli bir TL tutarı gir (en az 0,01). Örn. 1299,90";
    const options = variant ? { value: {} as Record<string, string> } : parsePairs(optionText, "Seçenekler", 10, 20, 30);
    if ("error" in options) e.options = options.error;
    setErrors(e);
    if (Object.keys(e).length > 0 || minor === null || !("value" in options)) return;
    setBusy(true);
    const result = variant
      ? await aSend<AdminVariant>("PATCH", `/admin/variants/${enc(variant.id)}`, {
        version: variant.version,
        ...(displayName.trim() !== variant.displayName ? { displayName: displayName.trim() } : {}),
        ...(minor !== variant.priceMinor ? { priceMinor: minor } : {}),
        ...(isActive !== variant.isActive ? { isActive } : {}),
      })
      : await aSend<AdminVariant>("POST", `/admin/products/${enc(productId)}/variants`, {
        sku: sku.trim(), displayName: displayName.trim(), priceMinor: minor, ...(Object.keys(options.value).length > 0 ? { options: options.value } : {}), isActive,
      });
    setBusy(false);
    if (result.ok) onDone(variant ? `${variant.sku} kaydedildi.` : `${sku.trim()} oluşturuldu.`); else setFailure(result);
  }
  const err = (key: string, apiKey = key) => errors[key] ?? fieldError(failure, apiKey);
  const takenCode = failure && (failure.code === "sku_taken" || failure.code === "variant_options_taken");
  const general = failure && failure.code !== "version_conflict" && !takenCode && Object.keys(failure.errors).length === 0 ? failure : null;
  const priceChanged = variant !== null && parseTlToMinor(price) !== null && parseTlToMinor(price) !== variant.priceMinor;
  return <form onSubmit={(ev) => void submit(ev)} noValidate aria-busy={busy} className="max-w-3xl rounded-lg border border-line bg-card p-4 sm:p-5" aria-label={variant ? `Varyantı düzenle: ${variant.sku}` : "Yeni varyant"}>
    <h3 className="mb-3 font-display text-lg font-bold">{variant ? `Varyantı düzenle: ${variant.sku}` : "Yeni varyant"}</h3>
    <div className="grid gap-4 sm:grid-cols-2">
      <Field label="SKU" id="v-sku" error={err("sku") ?? (failure?.code === "sku_taken" ? failure.message : undefined)} hint={variant ? "Oluşturulduktan sonra değişmez." : "Büyük harf, rakam ve tire; örn. TLF-X1-128-SYH"}>
        <input id="v-sku" value={sku} maxLength={40} disabled={!!variant} onChange={(ev) => setSku(ev.target.value.toUpperCase())} aria-invalid={!!err("sku")} aria-describedby={describedBy("v-sku", true, !!err("sku"))} className={`${inputClass} font-mono`} />
      </Field>
      <Field label="Varyant adı" id="v-ad" error={err("displayName")}><input id="v-ad" value={displayName} maxLength={160} onChange={(ev) => setDisplayName(ev.target.value)} aria-invalid={!!err("displayName")} className={inputClass} /></Field>
      <Field label="Fiyat (TL)" id="v-fiyat" error={err("price", "priceMinor")} hint="Kuruşlu yazılabilir: 1299,90">
        <input id="v-fiyat" inputMode="decimal" value={price} onChange={(ev) => setPrice(ev.target.value)} aria-invalid={!!err("price", "priceMinor")} aria-describedby={describedBy("v-fiyat", true, !!err("price", "priceMinor"))} className={`${inputClass} font-mono`} />
      </Field>
    </div>
    {!variant && <Field label="Seçenekler (isteğe bağlı)" id="v-secenek" className="mt-4" error={err("options") ?? (failure?.code === "variant_options_taken" ? failure.message : undefined)} hint="Her satıra bir seçenek: “Renk: Siyah”. Aynı seçenek kombinasyonu iki kez kullanılamaz.">
      <textarea id="v-secenek" rows={3} value={optionText} onChange={(ev) => setOptionText(ev.target.value)} aria-invalid={!!err("options")} aria-describedby={describedBy("v-secenek", true, !!err("options"))} className={`${textareaClass} font-mono text-sm`} />
    </Field>}
    <div className="mt-3"><CheckField id="v-aktif" label="Satışta (aktif)" checked={isActive} onChange={setActive} /></div>
    {priceChanged && <div className="mt-3"><Notice tone="warn" live="off">Fiyat değişikliği yeni sipariş önizlemelerini etkiler. Sepetinde eski fiyatı olan müşterilerin siparişi “fiyatlar değişti” uyarısıyla yeniden onaya düşer; verilmiş siparişler değişmez.</Notice></div>}
    {failure?.code === "version_conflict" && <div className="mt-4"><ConflictNotice onReload={() => { reload(); onCancel(); }} /></div>}
    {failure && takenCode && failure.code === "variant_options_taken" && <p role="alert" className="mt-3 text-sm text-red-800">{failure.message}</p>}
    {failure && takenCode && <p className="mt-1 text-sm text-fg-muted">İstek yanıtı kaybolduysa bu kayıt zaten oluşmuş olabilir; listeyi yenileyip kontrol et.</p>}
    {general && <div className="mt-4"><FailureBox failure={general} /></div>}
    <FormActions><SubmitButton busy={busy}>{variant ? "Varyantı kaydet" : "Varyant oluştur"}</SubmitButton><CancelButton onClick={onCancel} disabled={busy} />{failure && takenCode && <button type="button" onClick={reload} className={btnSmall}>Listeyi yenile</button>}</FormActions>
  </form>;
}

function Images({ product, reload, notice, setNotice }: { product: ProductDetail; reload: () => void; notice: string; setNotice: (message: string) => void }) {
  const assets = useRemote(() => aGet<ImageAsset[] | { items: ImageAsset[] }>("/admin/catalog/image-assets"), "gorsel-listesi");
  const [images, setImages] = useState<AdminImage[]>(() => [...product.images].sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0)));
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiFailure | null>(null);
  const [error, setError] = useState("");

  const move = (i: number, d: number) => setImages((list) => { const next = [...list]; const j = i + d; if (j < 0 || j >= next.length) return list; [next[i], next[j]] = [next[j], next[i]]; return next; });
  const remove = (i: number) => setImages((list) => { const next = list.filter((_, k) => k !== i); if (next.length > 0 && !next.some((x) => x.isPrimary)) next[0] = { ...next[0], isPrimary: true }; return next; });
  const primary = (i: number) => setImages((list) => list.map((x, k) => ({ ...x, isPrimary: k === i })));
  const alt = (i: number, value: string) => setImages((list) => list.map((x, k) => (k === i ? { ...x, altText: value } : x)));
  const add = (asset: ImageAsset) => setImages((list) => list.length >= 8 || list.some((x) => x.storageKey === asset.storageKey) ? list : [...list, { storageKey: asset.storageKey, altText: "", isPrimary: list.length === 0 }]);

  async function save() {
    setFailure(null); setNotice(""); setError("");
    if (images.some((x) => x.altText.trim() === "")) return setError("Her görsel için açıklama (alt metin) yaz.");
    if (images.some((x) => x.altText.length > 200)) return setError("Alt metin en çok 200 karakter olabilir.");
    if (images.length > 0 && images.filter((x) => x.isPrimary).length !== 1) return setError("Tam olarak bir ana görsel seç.");
    setBusy(true);
    const result = await aSend<ProductDetail>("PUT", `/admin/products/${enc(product.id)}/images`, { version: product.version, images: images.map((x) => ({ storageKey: x.storageKey, altText: x.altText.trim(), isPrimary: x.isPrimary })) });
    setBusy(false);
    if (result.ok) { setNotice("Görseller kaydedildi."); reload(); } else setFailure(result);
  }
  const taken = new Set(images.map((x) => x.storageKey));
  return <div className="space-y-5">
    {images.length === 0 ? <Empty title="Bu ürünün görseli yok">Aşağıdaki onaylı görsellerden ekle.</Empty> : <ol className="space-y-3" aria-label="Seçili görseller">
      {images.map((img, i) => <li key={img.storageKey} className="flex flex-wrap items-start gap-3 rounded-lg border border-line bg-card p-3">
        <Thumb url={imageSrc(img)} alt={img.altText || null} size={72} />
        <div className="min-w-0 flex-1 basis-56">
          <p className="mb-1 flex flex-wrap items-center gap-2"><Kunye>{img.storageKey}</Kunye>{img.isPrimary && <Chip tone="accent">Ana görsel</Chip>}</p>
          <label htmlFor={`alt-${i}`} className="mb-1 block text-sm font-semibold">Alt metin</label>
          <input id={`alt-${i}`} value={img.altText} maxLength={200} onChange={(ev) => alt(i, ev.target.value)} className={inputClass} />
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <label className="flex min-h-11 items-center gap-2 text-sm font-semibold"><input type="radio" name="ana-gorsel" checked={img.isPrimary} onChange={() => primary(i)} className="size-5 accent-[var(--color-ink)]" />Ana</label>
          <button type="button" className={btnSmall} disabled={i === 0} onClick={() => move(i, -1)} aria-label={`Yukarı taşı: ${img.storageKey}`}>Yukarı</button>
          <button type="button" className={btnSmall} disabled={i === images.length - 1} onClick={() => move(i, 1)} aria-label={`Aşağı taşı: ${img.storageKey}`}>Aşağı</button>
          <button type="button" className={btnSmall} onClick={() => remove(i)} aria-label={`Kaldır: ${img.storageKey}`}>Kaldır</button>
        </div>
      </li>)}
    </ol>}
    {error && <p role="alert" className="text-sm text-red-800">{error}</p>}
    {failure?.code === "version_conflict" && <ConflictNotice onReload={reload} />}
    {failure && failure.code !== "version_conflict" && <FailureBox failure={failure} />}
    {notice && <Notice tone="ok">{notice}</Notice>}
    <button type="button" onClick={() => void save()} disabled={busy} className={btnPrimary} data-testid="images-save">{busy ? "Kaydediliyor…" : "Görselleri kaydet"}</button>

    <div>
      <h3 className="mb-2 font-display text-lg font-bold">Onaylı görseller</h3>
      <Loaded remote={assets} label="Görsel listesi yükleniyor…">{(raw) => {
        const list = listOf(raw);
        return list.length === 0 ? <Empty title="Onaylı görsel listesi boş" /> : <ul className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
          {list.map((asset) => <li key={asset.storageKey} className="rounded-lg border border-line bg-card p-2">
            <Thumb url={imageSrc(asset)} alt={null} size={96} />
            <p className="mt-1 break-all font-mono text-xs">{asset.storageKey.split("/").pop()}</p>
            <p className="text-xs text-fg-muted">{asset.author}</p>
            <button aria-label={`${taken.has(asset.storageKey) ? "Eklendi" : "Ekle"}: ${asset.storageKey}`} type="button" className={`${btnSmall} mt-2 w-full`} disabled={taken.has(asset.storageKey) || images.length >= 8} onClick={() => add(asset)}>{taken.has(asset.storageKey) ? "Eklendi" : "Ekle"}</button>
          </li>)}
        </ul>;
      }}</Loaded>
    </div>
  </div>;
}
