"use client";

import { useState, type FormEvent } from "react";
import { aGet, aSend, enc } from "@/features/admin/client";
import { formatDateTime } from "@/features/admin/format";
import { listOf, type RoleChange, type StaffUser } from "@/features/admin/types";
import { useRemote } from "@/features/admin/use-remote";
import type { ApiFailure } from "@/lib/api";
import { isOrderId } from "@/features/orders/orders";
import { ConfirmDialog } from "@/components/ui/ConfirmDialog";
import { btnPrimary } from "@/components/ui/styles";
import { useAdmin } from "./AdminContext";
import { btnSmall, Chip, DataTable, Empty, FailureBox, Field, inputClass, Kunye, Loaded, Notice, PageHead, Section, type Column } from "./kit";

type Role = "Admin" | "Warehouse";
const roleLabel: Record<string, string> = { Admin: "Yönetici", Warehouse: "Depo" };
const rolesText = (roles: string[]) => (roles.length === 0 ? "Rol yok" : roles.map((r) => roleLabel[r] ?? r).join(", "));

// Personel rolleri HESAP KİMLİĞİYLE verilir (e-posta doğrulanmamıştır; kayıt açıktır). Kişi kimliğini Hesabım sayfasından okuyup yöneticiye iletir;
// yönetici burada e-postayı, "doğrulanmadı" uyarısını ve oluşturulma tarihini görüp onaylar. E-posta araması yoktur.
export function UsersAdmin() {
  const [lookup, setLookup] = useState<string | null>(null);
  const [idText, setIdText] = useState("");
  const [idError, setIdError] = useState("");
  const [version, setVersion] = useState(0); // rol değişince personel listesi yenilensin
  const staff = useRemote(() => aGet<StaffUser[] | { items: StaffUser[] }>("/admin/staff"), `staff-${version}`);

  function find(event: FormEvent) {
    event.preventDefault();
    const id = idText.trim().toLowerCase();
    if (!isOrderId(id)) return setIdError("Geçerli bir hesap kimliği gir (36 karakterlik UUID).");
    setIdError(""); setLookup(id);
  }
  const columns: Column<StaffUser>[] = [
    { key: "mail", header: "E-posta", primary: true, cell: (u) => <span className="break-all font-semibold">{u.email}</span> },
    { key: "roles", header: "Rol", cell: (u) => <span>{u.roles.filter((r) => r === "Admin" || r === "Warehouse").map((r) => <Chip key={r} tone={r === "Admin" ? "accent" : "neutral"}>{roleLabel[r]}</Chip>)}</span> },
    { key: "id", header: "Hesap kimliği", cell: (u) => <Kunye>{u.id}</Kunye> },
    { key: "act", header: "İşlem", cell: (u) => <button aria-label={`Yönet: ${u.email}`} type="button" className={btnSmall} onClick={() => { setIdText(u.id); setIdError(""); setLookup(u.id); }}>Yönet</button> },
  ];
  return <>
    <PageHead title="Kullanıcılar" lead="Personel yetkisi e-posta ile değil hesap kimliğiyle verilir. Yetki verilecek kişi kimliğini Hesabım sayfasından kopyalayıp sana iletir." />
    <Section id="kimlik" title="Hesap kimliğiyle bul">
      <form onSubmit={find} className="flex max-w-2xl flex-wrap items-end gap-3" noValidate>
        <Field label="Hesap kimliği" id="hesap-kimlik" error={idError} className="min-w-0 flex-1 basis-72">
          <input id="hesap-kimlik" value={idText} onChange={(e) => setIdText(e.target.value)} autoComplete="off" spellCheck={false} aria-invalid={!!idError} aria-describedby={idError ? "hesap-kimlik-error" : undefined} className={`${inputClass} font-mono`} placeholder="00000000-0000-0000-0000-000000000000" />
        </Field>
        <button type="submit" className={btnPrimary}>Hesabı göster</button>
      </form>
      {lookup && <div className="mt-5"><UserCard key={lookup} id={lookup} onChanged={() => setVersion((n) => n + 1)} /></div>}
    </Section>
    <Section id="personel" title="Mevcut personel" note="Yönetici ve depo rolü olan hesaplar.">
      <Loaded remote={staff} label="Personel yükleniyor…">{(raw) => {
        const rows = listOf(raw);
        return rows.length === 0 ? <Empty title="Personel listesi boş" /> : <DataTable caption="Personel" rows={rows} columns={columns} rowKey={(u) => u.id} />;
      }}</Loaded>
    </Section>
  </>;
}

function UserCard({ id, onChanged }: { id: string; onChanged: () => void }) {
  const { account } = useAdmin();
  const remote = useRemote(() => aGet<StaffUser>(`/admin/users/${enc(id)}`), id);
  const [pending, setPending] = useState<{ role: Role; grant: boolean } | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiFailure | null>(null);
  const [done, setDone] = useState("");

  async function apply(user: StaffUser) {
    if (!pending) return;
    setBusy(true); setFailure(null); setDone("");
    const result = await aSend<RoleChange>(pending.grant ? "PUT" : "DELETE", `/admin/users/${enc(user.id)}/roles/${pending.role}`);
    setBusy(false); setPending(null);
    if (!result.ok) { setFailure(result); return; }
    setDone(result.data.changed ? `${roleLabel[pending.role]} rolü ${pending.grant ? "verildi" : "kaldırıldı"}. Kullanıcının açık oturumları sonlandırıldı.` : `Değişiklik yok: hesap zaten bu durumda.`);
    remote.reload(); onChanged();
  }
  return <Loaded remote={remote} label="Hesap aranıyor…">{(user) => {
    const self = user.id === account.id;
    const isStaffRole = (r: string): r is Role => r === "Admin" || r === "Warehouse";
    const held = user.roles.filter(isStaffRole);
    return <div data-testid="user-card" className="max-w-2xl rounded-lg border border-line bg-card p-4 shadow-card sm:p-5">
      <dl className="grid gap-x-6 gap-y-2 sm:grid-cols-[auto_1fr]">
        <dt className="text-fg-muted">E-posta</dt><dd className="break-all font-semibold">{user.email}</dd>
        <dt className="text-fg-muted">Hesap kimliği</dt><dd><Kunye>{user.id}</Kunye></dd>
        <dt className="text-fg-muted">Oluşturuldu</dt><dd><time dateTime={user.createdAt}>{formatDateTime(user.createdAt)}</time></dd>
        <dt className="text-fg-muted">Roller</dt><dd>{rolesText(held)}</dd>
      </dl>
      {!user.emailConfirmed && <div className="mt-3"><Notice tone="warn" live="off" title="E-posta doğrulanmadı">Kayıt açık olduğu için bu adres herkes tarafından alınmış olabilir. Yetkiyi yalnızca kimliği hesap sahibinden bizzat aldığından eminsen ver.</Notice></div>}
      {self && <p className="mt-3 text-sm text-fg-muted">Bu senin hesabın; kendi yönetici rolünü kaldıramazsın.</p>}
      {done && <div className="mt-3"><Notice tone="ok">{done}</Notice></div>}
      {failure && <div className="mt-3"><FailureBox failure={failure} /></div>}
      <div className="mt-4 flex flex-wrap gap-3">
        {(["Admin", "Warehouse"] as Role[]).map((role) => held.includes(role)
          ? <button key={role} type="button" disabled={busy} onClick={() => setPending({ role, grant: false })} className={btnSmall} data-testid={`revoke-${role}`}>{roleLabel[role]} rolünü kaldır</button>
          : <button key={role} type="button" disabled={busy} onClick={() => setPending({ role, grant: true })} className={btnSmall} data-testid={`grant-${role}`}>{roleLabel[role]} rolü ver</button>)}
      </div>
      <ConfirmDialog open={pending !== null} busy={busy} busyLabel="Uygulanıyor…"
        title={pending ? `${roleLabel[pending.role]} rolü ${pending.grant ? "verilsin" : "kaldırılsın"} mı?` : ""}
        description={pending ? `${user.email} (${user.emailConfirmed ? "e-posta doğrulanmış" : "e-posta doğrulanmadı"}) hesabı için ${roleLabel[pending.role]} rolü ${pending.grant ? "verilecek" : "kaldırılacak"}. Kullanıcının açık oturumları sonlandırılır ve işlem denetim kaydına yazılır.` : ""}
        confirmLabel={pending?.grant ? "Rolü ver" : "Rolü kaldır"} onConfirm={() => void apply(user)} onCancel={() => setPending(null)} />
    </div>;
  }}</Loaded>;
}
