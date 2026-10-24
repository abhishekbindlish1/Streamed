// Report public signing metadata only. This never opens a P12 or private key.
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { X509Certificate, createHash } = require('node:crypto');

try {
  if (!process.argv[2]) throw new Error('Usage: node scripts/inspect-tv-signing.js path/to/working.tpk (or .wgt)');
  const file = path.resolve(process.argv[2]);
  const result = { package: path.basename(file), signatures: {} };
  for (const [role, entry] of [['author', 'author-signature.xml'], ['distributor', 'signature1.xml']]) {
    const xml = execFileSync('unzip', ['-p', file, entry], { encoding: 'utf8', maxBuffer: 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'] });
    const certificates = [...xml.matchAll(/<(?:[\w.-]+:)?X509Certificate\b[^>]*>([\s\S]*?)<\/(?:[\w.-]+:)?X509Certificate>/g)];
    if (!certificates.length) throw new Error(`${entry} has no certificates`);
    result.signatures[role] = certificates.map(([, encoded]) => {
      const certificate = new X509Certificate(Buffer.from(encoded.replace(/\s/g, ''), 'base64'));
      const from = new Date(certificate.validFrom), to = new Date(certificate.validTo);
      return {
        certificateSha256: certificate.fingerprint256,
        publicKeySha256: createHash('sha256').update(certificate.publicKey.export({ type: 'spki', format: 'der' })).digest('hex'),
        issuer: certificate.issuer,
        validFrom: from.toISOString(), validUntil: to.toISOString(),
        currentlyValid: from <= new Date() && new Date() < to
      };
    });
  }
  console.log(JSON.stringify(result, null, 2));
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
