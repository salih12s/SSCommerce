import { isIP } from "node:net";

function address(value) {
  if (typeof value !== "string") return null;
  const trimmed = value.trim();
  const plain = trimmed.startsWith("::ffff:") ? trimmed.slice(7) : trimmed;
  return isIP(plain) ? plain : null;
}

export function trustedProxyAddresses(value = "") {
  return new Set(value.split(",").filter(Boolean).map((item) => {
    const ip = address(item);
    if (!ip) throw new Error("WEB_TRUSTED_PROXIES must contain exact IP addresses.");
    return ip;
  }));
}

// Only a configured peer may supply a chain. Walk from the socket towards the
// client and stop at the first untrusted hop; never use an arbitrary leftmost IP.
export function sanitizeForwardedHeaders(request, trusted = new Set()) {
  const peer = address(request.socket.remoteAddress);
  if (!peer) throw new Error("Missing client connection address.");
  let client = peer;
  const raw = request.headers["x-forwarded-for"];
  if (trusted.has(peer) && typeof raw === "string" && raw.length <= 2048) {
    const hops = raw.split(",").map(address);
    if (hops.length <= 32 && hops.every(Boolean)) {
      while (hops.length && trusted.has(client)) client = hops.pop();
    }
  }
  const proto = request.headers["x-forwarded-proto"];
  request.headers["x-forwarded-for"] = client;
  request.headers["x-forwarded-proto"] = trusted.has(peer) && (proto === "https" || proto === "http")
    ? proto : request.socket.encrypted ? "https" : "http";
  request.headers["x-forwarded-host"] = request.headers.host ?? "";
  delete request.headers.forwarded;
  delete request.headers["x-forwarded-port"];
  delete request.headers["x-real-ip"];
  for (const key of Object.keys(request.headers)) {
    if (key.startsWith("x-original-")) delete request.headers[key];
  }
}
