const http = require('node:http');
const fs = require('node:fs/promises');
const path = require('node:path');
const { buildWeb } = require('../apps/web/scripts/build-web');
const { randomUUID } = require('node:crypto');

async function createSandbox({ pairing = false } = {}) {
  const { site } = await buildWeb();
  const fixture = JSON.parse(await fs.readFile(path.join(site, 'data/live-demo.json'), 'utf8'));
  const apiMatches = fixture.matches.map((match, index) => ({
    id: match.id, title: match.title, category: match.sport, date: Date.now() - 60000,
    popular: index < 3, sources: [{ source: 'demo', id: match.id }]
  }));
  const mime = { '.html': 'text/html', '.css': 'text/css', '.js': 'text/javascript', '.json': 'application/json', '.svg': 'image/svg+xml', '.png': 'image/png' };
  const pairs = new Map();
  let failPairInit = false;
  return http.createServer(async (req, res) => {
    try {
      const url = new URL(req.url, 'http://sandbox.invalid');
      const json = data => { res.writeHead(200, { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*' }); res.end(JSON.stringify(data)); };
      if (url.pathname === '/health') return json({ status: 'ok', mode: 'sandbox', authentication: pairing ? 'test-pairing-fixture' : 'disabled-fixtures-only' });
      if (pairing && req.method === 'POST' && url.pathname === '/v1/auth/pair/init') {
        if (failPairInit) { failPairInit = false; res.writeHead(503); res.end('Fixture outage'); return; }
        const pollToken = randomUUID();
        pairs.set(pollToken, { complete: false });
        return json({ code: '123456', pollToken, expiresAt: new Date(Date.now() + 300000).toISOString() });
      }
      if (pairing && url.pathname === '/v1/auth/pair/poll') {
        const record = pairs.get(url.searchParams.get('token'));
        return json(!record ? { status: 'expired' } : record.complete ? { status: 'complete', jwt: 'sandbox-fixture-session-not-a-production-jwt' } : { status: 'pending' });
      }
      if (pairing && req.method === 'POST' && url.pathname === '/__test/pair/complete') {
        for (const record of pairs.values()) record.complete = true;
        return json({ sandbox: true, completed: pairs.size });
      }
      if (pairing && req.method === 'POST' && url.pathname === '/__test/pair/fail-next-init') {
        failPairInit = true;
        return json({ sandbox: true });
      }
      if (url.pathname === '/media/native-player.html') {
        res.writeHead(200, { 'Content-Type': 'text/html' });
        res.end('<!doctype html><html><body style="margin:0;background:black"><video style="width:100%;height:100vh" controls autoplay loop src="/media/native-test.mp4"></video></body></html>');
        return;
      }
      if (/^\/media\/native-test(?:-\d{2})?\.(mp4|m3u8|ts)$/.test(url.pathname)) {
        const media = await fs.readFile(path.join(__dirname, '../tests/fixtures', url.pathname));
        const contentType = url.pathname.endsWith('.mp4') ? 'video/mp4' : url.pathname.endsWith('.m3u8') ? 'application/vnd.apple.mpegurl' : 'video/mp2t';
        const range = req.headers.range?.match(/^bytes=(\d+)-(\d*)$/);
        let start = range ? Number(range[1]) : 0;
        let end = range?.[2] ? Number(range[2]) : media.length - 1;
        if (start > end || start >= media.length || end >= media.length) { res.writeHead(416, { 'Content-Range': `bytes */${media.length}` }); res.end(); return; }
        const headers = { 'Content-Type': contentType, 'Accept-Ranges': 'bytes', 'Content-Length': end - start + 1, 'Access-Control-Allow-Origin': '*' };
        if (range) headers['Content-Range'] = `bytes ${start}-${end}/${media.length}`;
        res.writeHead(range ? 206 : 200, headers);
        res.end(req.method === 'HEAD' ? undefined : media.subarray(start, end + 1));
        return;
      }
      if (url.pathname === '/api/sports') return json(fixture.sports);
      if (url.pathname.startsWith('/api/matches/')) {
        const sport = url.pathname.split('/')[3];
        return json(fixture.sports.some(item => item.id === sport) ? apiMatches.filter(match => match.category === sport) : apiMatches);
      }
      if (url.pathname.startsWith('/api/stream/demo/')) {
        const match = fixture.matches.find(item => item.id === decodeURIComponent(url.pathname.slice('/api/stream/demo/'.length)));
        if (!match) { res.writeHead(404); res.end(); return; }
        const media = match.id === 'live-cricket-2' ? 'native-test.m3u8' : 'native-test.mp4';
        return json([{ id: `${match.id}-0`, streamNo: 1, language: 'Test pattern', hd: false, source: 'demo', embedUrl: `http://${req.headers.host}/media/${media}` }]);
      }
      if (url.pathname === '/v1/config') return json({ streamedBaseUrl: '/', useBackendStreamProxy: false, sandbox: true });
      const file = path.resolve(site, decodeURIComponent(url.pathname).replace(/^\/+/, '') || 'index.html');
      if (!file.startsWith(site + path.sep)) { res.writeHead(403); res.end(); return; }
      const data = await fs.readFile(file);
      res.writeHead(200, { 'Content-Type': mime[path.extname(file)] || 'application/octet-stream' }); res.end(data);
    } catch { res.writeHead(404); res.end('Not found'); }
  });
}

if (require.main === module) {
  createSandbox({ pairing: process.env.SANDBOX_PAIRING_FIXTURE === 'true' }).then(server => server.listen(Number(process.env.SANDBOX_PORT || 8080), process.env.SANDBOX_HOST || '127.0.0.1', () => {
    console.log('Fixture sandbox running; web UI at /, catalog API at /api/, health at /health.');
  })).catch(error => { console.error(error.message); process.exitCode = 1; });
}
module.exports = { createSandbox };
