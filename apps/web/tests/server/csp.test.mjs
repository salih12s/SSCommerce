import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { collectInlineScriptHashes } from "../../server/csp.mjs";

const sha = (body) => `'sha256-${createHash("sha256").update(body).digest("base64")}'`;

test("derleme kabuğundaki nonce'suz satır içi betikler özetlenir; src'li, nonce'lu ve boş betikler atlanır", () => {
  const dir = mkdtempSync(path.join(tmpdir(), "csp-"));
  try {
    mkdirSync(path.join(dir, "app", "alt"), { recursive: true });
    writeFileSync(path.join(dir, "app", "a.html"), '<script src="/x.js" async=""></script><script>one()</script><script id="_R_">two()</script><script nonce="abc">nonced()</script><script> </script>');
    writeFileSync(path.join(dir, "app", "alt", "b.html"), "<script>one()</script><script>three()</script>");
    writeFileSync(path.join(dir, "app", "ignored.txt"), "<script>nope()</script>");
    const hashes = collectInlineScriptHashes(dir);
    assert.deepEqual(hashes, [sha("one()"), sha("two()"), sha("three()")].sort());
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test("olmayan dizin boş liste verir (derleme yoksa sunucu yine açılır)", () => {
  assert.deepEqual(collectInlineScriptHashes(path.join(tmpdir(), "yok-boyle-bir-dizin-csp")), []);
});
