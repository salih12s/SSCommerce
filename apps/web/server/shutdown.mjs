/**
 * Zarif kapanış: yeni bağlantı kabul etmeyi bırakır, sürmekte olan isteklerin bitmesini en çok `graceMs` bekler,
 * sonra kalan bağlantıları kapatır. Boştaki keep-alive bağlantıları hemen kapatılır.
 * Dönüş: { forced } — süre dolduğu için bağlantı koparıldı mı.
 */
export function gracefulClose(server, { graceMs = 10_000, pollMs = 50 } = {}) {
  return new Promise((resolve) => {
    let forced = false;
    let done = false;
    const finish = () => {
      if (done) return;
      done = true;
      clearTimeout(deadline);
      clearInterval(poll);
      resolve({ forced });
    };
    const poll = setInterval(() => server.closeIdleConnections(), pollMs);
    const deadline = setTimeout(() => {
      forced = true;
      server.closeAllConnections();
    }, graceMs);
    server.close(finish);
    server.closeIdleConnections();
  });
}
