"use client";

import { useRef, useState, type FormEvent, type ReactNode } from "react";
import { createAddress, updateAddress, type Address, type AddressInput } from "@/features/account/addresses";
import { provinces } from "@/features/account/provinces";
import { btnPrimary, btnSecondary, fieldClass } from "@/components/ui/styles";

const order = ["label", "recipientName", "phone", "province", "district", "streetAddress", "postalCode"] as const;
type FieldName = (typeof order)[number];

// Adres formu: yalnızca sunucu doğrular (noValidate); alan hataları ilgili alanın altında gösterilir. Kişisel veri URL'ye ya da
// tarayıcı depolamasına yazılmaz; yalnızca JSON gövdesinde gider.
export function AddressForm({ initial, idPrefix, submitLabel, onSaved, onCancel }: {
  initial?: Address; idPrefix: string; submitLabel: string; onSaved: (address: Address) => void; onCancel?: () => void;
}) {
  const formRef = useRef<HTMLFormElement>(null);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [general, setGeneral] = useState("");
  const id = (name: string) => `${idPrefix}-${name}`;

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;
    const data = new FormData(event.currentTarget);
    const text = (name: FieldName) => String(data.get(name) ?? "");
    const body: AddressInput = {
      label: text("label"), recipientName: text("recipientName"), phone: text("phone"), province: text("province"),
      district: text("district"), streetAddress: text("streetAddress"), postalCode: text("postalCode").trim() === "" ? null : text("postalCode"),
    };
    setBusy(true); setErrors({}); setGeneral("");
    const result = initial ? await updateAddress(initial.id, body) : await createAddress(body);
    setBusy(false);
    if (result.ok) { onSaved(result.data); return; }
    const fieldErrors = Object.fromEntries(Object.entries(result.errors).map(([key, value]) => [key.charAt(0).toLowerCase() + key.slice(1), value]));
    setErrors(fieldErrors);
    const first = order.find((name) => fieldErrors[name]);
    if (first) document.getElementById(id(first))?.focus();
    else setGeneral(result.message);
  }

  const err = (name: FieldName) => errors[name]?.join(" ");
  const aria = (name: FieldName) => ({ "aria-invalid": err(name) ? true : undefined, "aria-describedby": err(name) ? id(`${name}-error`) : undefined });
  const field = (name: FieldName, label: string, control: ReactNode, hint?: string) => (
    <div>
      <label htmlFor={id(name)} className="mb-1 block font-semibold">{label}</label>
      {control}
      {hint && <p className="mt-1 text-sm text-fg-muted">{hint}</p>}
      {err(name) && <p id={id(`${name}-error`)} className="mt-1 text-sm text-red-700">{err(name)}</p>}
    </div>
  );
  const input = `h-12 ${fieldClass}`;
  return (
    <form ref={formRef} onSubmit={submit} noValidate aria-busy={busy} className="space-y-4">
      {field("label", "Adres adı", <input id={id("label")} name="label" defaultValue={initial?.label} maxLength={50} autoComplete="off" placeholder="Ev, iş…" className={input} {...aria("label")} />)}
      {field("recipientName", "Alıcı adı soyadı", <input id={id("recipientName")} name="recipientName" defaultValue={initial?.recipientName} maxLength={100} autoComplete="name" className={input} {...aria("recipientName")} />)}
      {field("phone", "Cep telefonu", <input id={id("phone")} name="phone" type="tel" inputMode="tel" defaultValue={initial?.phone} maxLength={20} autoComplete="tel-national" className={input} {...aria("phone")} />, "Örnek: 5321234567 ya da 0532 123 45 67")}
      {field("province", "İl", <select id={id("province")} name="province" defaultValue={initial?.province ?? ""} autoComplete="address-level1" className={input} {...aria("province")}><option value="">İl seç</option>{provinces.map((p) => <option key={p} value={p}>{p}</option>)}</select>)}
      {field("district", "İlçe", <input id={id("district")} name="district" defaultValue={initial?.district} maxLength={64} autoComplete="address-level2" className={input} {...aria("district")} />)}
      {field("streetAddress", "Açık adres", <textarea id={id("streetAddress")} name="streetAddress" defaultValue={initial?.streetAddress} maxLength={300} rows={3} autoComplete="street-address" className={`${fieldClass} py-2`} {...aria("streetAddress")} />, "Mahalle, sokak, bina ve daire no.")}
      {field("postalCode", "Posta kodu (isteğe bağlı)", <input id={id("postalCode")} name="postalCode" inputMode="numeric" defaultValue={initial?.postalCode ?? ""} maxLength={5} autoComplete="postal-code" className={input} {...aria("postalCode")} />)}
      {general && <p role="alert" className="rounded-md border border-red-300 bg-red-50 p-3 text-sm text-red-800">{general}</p>}
      <div className="flex flex-wrap gap-3">
        <button type="submit" disabled={busy} className={btnPrimary}>{busy ? "Kaydediliyor…" : submitLabel}</button>
        {onCancel && <button type="button" onClick={onCancel} disabled={busy} className={btnSecondary}>Vazgeç</button>}
      </div>
    </form>
  );
}
