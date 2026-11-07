import assert from "node:assert/strict";
import test from "node:test";
import { minorToTlInput, parseTlToMinor } from "../../src/lib/admin/money.ts";

test("TL input converts to integer kuruş without floating point", () => {
  const ok = { "1299": 129900, "1299,5": 129950, "1299,50": 129950, "1.299,50": 129950, "1,299.50": 129950, "1299.50": 129950, "1.299": 129900, "1.299.000": 129900000, "0,01": 1, "0.1": 10, " 12 ": 1200, "19,99": 1999, "9007199254740991": null };
  for (const [input, minor] of Object.entries(ok)) assert.equal(parseTlToMinor(input), minor, input);
  assert.equal(parseTlToMinor("90071992547409,91"), 9007199254740991);
});

test("invalid TL input is rejected", () => {
  for (const input of ["", " ", "0", "0,00", "-5", "1,234", "1,2,3", "abc", "12,5x", "1.29,50", "1e5", "NaN", "Infinity", "9999999999999999", "90071992547409,92", "١٢"]) assert.equal(parseTlToMinor(input), null, input);
});

test("kuruş formats back to an editable TL string that parses to the same value", () => {
  for (const minor of [1, 99, 100, 129900, 129950, 1999, 9007199254740991]) assert.equal(parseTlToMinor(minorToTlInput(minor)), minor, String(minor));
  assert.equal(minorToTlInput(129950), "1299,50");
  assert.equal(minorToTlInput(129900), "1299");
});

// Regresyon (QA bulgusu, Medium): "0.500" gibi başı sıfır olan nokta+3 hane binlik ayracı sayılıp 1000 kat pahalı fiyat kaydediyordu.
// Binlik grubu 1-9 ile başlar; başı sıfır olan gruplar ondalık niyetidir ve 3 haneli kesir geçersiz olduğundan reddedilir.
test("a leading-zero dot group is never read as a thousands separator", () => {
  for (const input of ["0.500", "0.100", "000.100", "00.500", "0.500,00", "0.001", "0.999"]) assert.equal(parseTlToMinor(input), null, input);
  // Gerçek Türkçe binlik gösterimi ve normal ondalıklar değişmedi.
  assert.equal(parseTlToMinor("1.299"), 129900);
  assert.equal(parseTlToMinor("10.500"), 1050000);
  assert.equal(parseTlToMinor("999.999"), 99999900);
  assert.equal(parseTlToMinor("0.5"), 50);
  assert.equal(parseTlToMinor("0.50"), 50);
  assert.equal(parseTlToMinor("0,5"), 50);
  assert.equal(parseTlToMinor("0,07"), 7);
});
