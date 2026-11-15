// Node check of wwwroot/binaryChunks.js (ADR 0272 §5): usage node binaryChunks.test.mjs <module> <scenario>
const [modulePath, scenario] = process.argv.slice(2);
const { toJsonForm, toBinaryForm, sha256Hex } = await import(new URL(`file://${modulePath}`).href);
const hello = new TextEncoder().encode("hello");
const helloHash = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
const fail = message => { console.log(message); process.exit(1); };

if (scenario === "round-trip") {
    const json = toJsonForm([{ contentHash: "h", chunkIndex: 0, data: hello, chunkHash: helloHash, createdUtcMs: 1 }]);
    if (json[0].dataBase64 !== "aGVsbG8=" || "data" in json[0]) fail(JSON.stringify(json));
    const binary = await toBinaryForm(json[0]);
    if (binary.chunkHash !== helloHash || new TextDecoder().decode(binary.data) !== "hello" || "dataBase64" in binary) fail("binary form");
    if (toJsonForm({ key: "policyRevision", value: 0 }).value !== 0) fail("other records kept");
} else if (scenario === "hash-mismatch") {
    try { await toBinaryForm({ contentHash: "h", chunkIndex: 0, dataBase64: "aGVsbG8=", chunkHash: "00" }); fail("accepted"); }
    catch (error) { if (!String(error.message).includes("indexeddb.chunk-hash-mismatch")) fail(error.message); }
} else if (scenario === "malformed") {
    try { await toBinaryForm({ contentHash: "h", chunkIndex: 0, dataBase64: "%%%" }); fail("accepted"); }
    catch (error) { if (!String(error.message).includes("indexeddb.chunk-malformed")) fail(error.message); }
} else if (scenario === "legacy-without-hash") {
    const binary = await toBinaryForm({ contentHash: "h", chunkIndex: 0, dataBase64: "aGVsbG8=" });
    if (binary.chunkHash !== await sha256Hex(hello)) fail("hash not computed");
} else {
    fail(`unknown scenario ${scenario}`);
}
console.log("ok");
