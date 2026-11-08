# Sınırlı yük, tutarlılık ve kesinti testleri

Üretim-eşdeğeri yalıtılmış yığına (Caddy TLS 8443 -> Next -> ASP.NET -> PostgreSQL) karşı koşar. Node 24, yeni bağımlılık yok (yerleşik `fetch`, `node:perf_hooks`).
Bu bir TABAN ölçümüdür: istemci ve sunucu aynı makinede; iş hedefi/SLA değildir.

## Ön koşul

- Yığın `deploy/compose.perf.yml` katmanıyla ayakta (IP başına hız sınırları yükseltilmiş; sipariş sınırları üretim varsayılanında). Üretimde KULLANILMAZ.
- `PROD_CREDS_FILE`: `PROD_BASE_URL`, `PROD_ADMIN_EMAIL`, `PROD_ADMIN_PASSWORD` içeren KEY=VALUE dosyası (depo dışında; değerler yazdırılmaz).
- `NODE_EXTRA_CA_CERTS`: yerel CA kökü (sertifika doğrulaması kapatılmaz).
- `LOADTEST_OUT` (isteğe bağlı): JSON sonuç dizini (varsayılan: işletim sistemi geçici dizini; depoya yazılmaz).
- Docker erişimi: değişmez sorgusu `docker compose exec db psql` (`ssc_backup`, yalnız okuma), metrik `docker run` ile iç porttan GET.

## Çalıştırma

```
node deploy/loadtest/scenarios.mjs <S1|S2|S3|S4|S5|S6|S7|S8|K1|all|metrics>
```

`all` = S1..S8 sırayla. `K1` (kesinti drill'i) ayrı çağrılır: `docker stop/start sscommerce-stack-db-1` yapar, yalnız bu yığında, tek kullanımlık veriyle.

| Senaryo | Ölçer / doğrular |
|---|---|
| S1 | Vitrin HTML + katalog API karışımı, eşzamanlılık 16 ve 64 (30 sn); rps, p50/p95/p99, durum dağılımı; 5 Hz `/readyz` |
| S2 | 150 kullanıcı kayıt+giriş+`/auth/me`, eşzamanlılık 16 |
| S3 | 150 kullanıcı sipariş akışı (stok 400): stok düşüşü == sipariş adedi, negatif stok yok, fiyat anlık görüntüsü |
| S4 | Son birim yarışı x3: stok 3, 40 eşzamanlı sipariş -> tam 3x201, 37x409 insufficient_stock |
| S5 | Aynı Idempotency-Key ile 6 eşzamanlı istek x30 kullanıcı; farklı gövde -> 409 idempotency_key_reused |
| S6 | 30 siparişin eşzamanlı çift iptali; stok bir kez geri gelir |
| S7 | open_order_limit (3), kullanıcılar arası bağımsızlık, order_daily_limit (429 + Retry-After) |
| S8 | Admin sipariş listesi + pano + stok hareketleri, eşzamanlılık 8, 20 sn |
| K1 | Sürekli sipariş yükü sırasında DB 12 sn kapalı; 5xx dağılımı, toparlanma süresi, aynı anahtarla yeniden gönderimde çift sipariş yok |

Her senaryo sonunda `deploy/db/invariants.sql` (+ kullanıcı başına tek idempotency anahtarı kuralı) canlı DB'de salt okunur çalışır; çıktının sonunda `ssc_orders_total` ve `http_server_requests_total` sayaç farkları, istemcinin gördüğü `POST /orders` durumlarıyla yan yana yazılır.
