import { defineConfig, globalIgnores } from "eslint/config";
import nextVitals from "eslint-config-next/core-web-vitals";
import nextTs from "eslint-config-next/typescript";

const eslintConfig = defineConfig([
  ...nextVitals,
  ...nextTs,
  // Yönetici tarafından girilen metin asla HTML olarak yazılmaz (Faz 8 sözleşmesi).
  { rules: { "react/no-danger": "error" } },
  // Mimari sınır (Bulletproof React): özellikler arası bağımlılık yönü sabit; ortak katman özellik/app içe aktarmaz.
  // Barrel dosyası yok; doğrudan dosya import'u. Alias (@/) eslint-import-resolver-typescript ile çözülür.
  {
    rules: {
      "import/no-restricted-paths": [
        "error",
        {
          zones: [
            { target: "./src/features/favorites", from: "./src/features", except: ["./favorites"], message: "features/favorites yalnız ortak katmanı içe aktarabilir (Bulletproof React sınırı)." },
            { target: "./src/features/catalog", from: "./src/features", except: ["./catalog", "./favorites"], message: "features/catalog yalnız ortak katmanı ve şu özellikleri içe aktarabilir: favorites (Bulletproof React sınırı)." },
            { target: "./src/features/cart", from: "./src/features", except: ["./cart", "./catalog"], message: "features/cart yalnız ortak katmanı ve şu özellikleri içe aktarabilir: catalog (Bulletproof React sınırı)." },
            { target: "./src/features/auth", from: "./src/features", except: ["./auth", "./cart", "./favorites"], message: "features/auth yalnız ortak katmanı ve şu özellikleri içe aktarabilir: cart, favorites (Bulletproof React sınırı)." },
            { target: "./src/features/account", from: "./src/features", except: ["./account", "./auth", "./cart", "./favorites", "./orders"], message: "features/account yalnız ortak katmanı ve şu özellikleri içe aktarabilir: auth, cart, favorites, orders (Bulletproof React sınırı)." },
            { target: "./src/features/orders", from: "./src/features", except: ["./orders"], message: "features/orders yalnız ortak katmanı içe aktarabilir (Bulletproof React sınırı)." },
            { target: "./src/features/checkout", from: "./src/features", except: ["./checkout", "./cart", "./account", "./auth", "./orders"], message: "features/checkout yalnız ortak katmanı ve şu özellikleri içe aktarabilir: cart, account, auth, orders (Bulletproof React sınırı)." },
            { target: "./src/features/home", from: "./src/features", except: ["./home"], message: "features/home yalnız ortak katmanı içe aktarabilir (Bulletproof React sınırı)." },
            { target: "./src/features/shell", from: "./src/features", except: ["./shell", "./catalog", "./cart", "./favorites"], message: "features/shell yalnız ortak katmanı ve şu özellikleri içe aktarabilir: catalog, cart, favorites (Bulletproof React sınırı)." },
            { target: "./src/features/admin", from: "./src/features", except: ["./admin", "./auth", "./cart", "./orders", "./favorites"], message: "features/admin yalnız ortak katmanı ve şu özellikleri içe aktarabilir: auth, cart, orders, favorites (Bulletproof React sınırı)." },
            { target: ["./src/lib", "./src/components", "./src/content"], from: ["./src/features", "./src/app"], message: "Ortak katman (lib, components, content) özellik ya da app dosyası içe aktaramaz." },
          ],
        },
      ],
    },
  },
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
