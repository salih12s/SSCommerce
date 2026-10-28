# SSCommerce web (`apps/web`)

Next.js (App Router) + React + TypeScript + Tailwind CSS. Ortam değişkenleri için `.env.example` dosyasını `.env.local` olarak kopyala.

```bash
npm install
npm run dev        # geliştirme sunucusu (http://localhost:3000)
npm run lint       # ESLint
npm run typecheck  # tsc --noEmit
npm run build      # üretim derlemesi
npm run start      # üretim sunucusu
```

`/api/backend/*` istekleri `API_BASE_URL` adresindeki API'ye yönlendirilir (varsayılan `http://localhost:5080`). `/status` sayfası backend health uçlarını gösterir.
