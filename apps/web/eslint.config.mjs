import { defineConfig, globalIgnores } from "eslint/config";
import nextVitals from "eslint-config-next/core-web-vitals";
import nextTs from "eslint-config-next/typescript";

const eslintConfig = defineConfig([
  ...nextVitals,
  ...nextTs,
  // Yönetici tarafından girilen metin asla HTML olarak yazılmaz (Faz 8 sözleşmesi).
  { rules: { "react/no-danger": "error" } },
  // Override default ignores of eslint-config-next.
  globalIgnores([
    // Default ignores of eslint-config-next:
    ".next/**",
    "out/**",
    "build/**",
    "next-env.d.ts",
    // Playwright çıktısı: koşu sırasında silinip yeniden oluşturulur (lint ENOENT ile düşmesin) ve kaynak kod değildir.
    "test-results/**",
    "playwright-report/**",
  ]),
]);

export default eslintConfig;
