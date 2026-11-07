// TL girdisi <-> kuruş. Yalnız gönderirken TL'den kuruşa çevrilir (tam sayı aritmetiği; kayan nokta yok); gösterim `...Minor` alanlarından yapılır.
export const MAX_MINOR = 9_007_199_254_740_991;

/** "1299", "1299,5", "1.299,50", "1299.50" -> kuruş. Geçersiz, 0, negatif, 2'den çok ondalık ya da güvenli tam sayıyı aşan değer null. */
export function parseTlToMinor(input: string): number | null {
  let text = input.trim().replace(/\s+/g, "");
  if (text === "") return null;
  const hasComma = text.includes(",");
  const hasDot = text.includes(".");
  if (hasComma && hasDot) {
    // Son ayraç ondalıktır; öteki binlik ayracıdır.
    const decimalSep = text.lastIndexOf(",") > text.lastIndexOf(".") ? "," : ".";
    const thousandSep = decimalSep === "," ? "." : ",";
    // Binlik ayracı varsa gruplar 3 haneli olmalı: "1.29,50" geçersizdir.
    const head = text.slice(0, text.lastIndexOf(decimalSep)).split(thousandSep);
    if (head.length > 1 && (head[0].length > 3 || head[0].startsWith("0") || head.slice(1).some((g) => g.length !== 3))) return null;
    text = text.split(thousandSep).join("").replace(decimalSep, ".");
  } else if (hasComma) {
    if ((text.match(/,/g) ?? []).length > 1) return null;
    text = text.replace(",", ".");
  } else if (hasDot) {
    const dots = (text.match(/\./g) ?? []).length;
    // "1.299" ve "1.299.000" binlik ayracı; "1.5" ve "1299.50" ondalıktır.
    // Başı sıfır olan grup ("0.500", "000.100") binlik olamaz: ondalık niyetidir, 3 haneli kesir geçersiz sayılır (1000 kat fiyat hatasını önler).
    if (/^[1-9]\d{0,2}(\.\d{3})+$/.test(text)) text = text.replace(/\./g, "");
    else if (dots > 1) return null;
  }
  const match = /^(\d+)(?:\.(\d{1,2}))?$/.exec(text);
  if (!match) return null;
  const whole = match[1];
  if (whole.length > 16) return null;
  const minor = Number(whole) * 100 + Number((match[2] ?? "").padEnd(2, "0") || "0");
  return Number.isSafeInteger(minor) && minor >= 1 && minor <= MAX_MINOR ? minor : null;
}

/** Kuruş -> düzenlenebilir TL metni ("129900" -> "1299", "129950" -> "1299,50"). */
export function minorToTlInput(minor: number): string {
  const whole = Math.trunc(minor / 100);
  const cents = minor % 100;
  return cents === 0 ? String(whole) : `${whole},${String(cents).padStart(2, "0")}`;
}
