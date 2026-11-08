-- İş değişmezleri: her satır "kural|ihlal sayısı" döner; 0 beklenir. Veri satırı/PII ÇIKTIYA YAZILMAZ, yalnız sayılar.
-- Hem canlı veritabanında hem geri yüklenmiş kopyada çalıştırılır (yedek doğrulaması).
select 'stok_negatif_degil|' || count(*) from inventory_balances where on_hand_quantity < 0;

select 'stok_bakiye_eşittir_hareket_toplami|' || count(*) from (
  select b.id from inventory_balances b
  left join (select warehouse_id, product_variant_id, sum(quantity_delta) as total from stock_movements group by 1, 2) m
    on m.warehouse_id = b.warehouse_id and m.product_variant_id = b.product_variant_id
  where b.on_hand_quantity <> coalesce(m.total, 0)
) x;

select 'siparis_ara_toplam_eşittir_kalemler|' || count(*) from (
  select o.id from orders o
  left join (select order_id, sum(line_subtotal) as total from order_items group by 1) i on i.order_id = o.id
  where o.item_subtotal <> coalesce(i.total, 0)
) x;

select 'kalem_tutari_eşittir_birim_x_adet|' || count(*) from order_items where line_subtotal <> unit_price * quantity;

select 'siparis_tahsis_adedi_eşittir_kalem_adedi|' || count(*) from (
  select i.id from order_items i
  left join (select order_item_id, sum(quantity) as q from order_inventory_allocations group by 1) a on a.order_item_id = i.id
  where i.quantity <> coalesce(a.q, 0)
) x;

select 'tek_idempotency_anahtari_kullanici_basina|' || count(*) from (
  select user_id, idempotency_key from orders group by 1, 2 having count(*) > 1
) x;
