const LIMIT = 12 * 1024 * 1024;
const enc = new TextEncoder();

async function authorized(request, secret) {
  if (!secret || secret.length < 32) return false;
  const key = await crypto.subtle.importKey('raw', enc.encode(secret), {name:'HMAC', hash:'SHA-256'}, false, ['sign','verify']);
  const expected = await crypto.subtle.sign('HMAC', key, enc.encode(`Bearer ${secret}`));
  return crypto.subtle.verify('HMAC', key, expected, enc.encode(request.headers.get('Authorization') || ''));
}

async function boundedBody(request) {
  const reader = request.body?.getReader();
  if (!reader) throw new Error('empty');
  const chunks = []; let size = 0;
  try {
    while (true) {
      const {value, done} = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > LIMIT) throw new Error('size');
      chunks.push(value);
    }
  } finally { await reader.cancel(); }
  const bytes = new Uint8Array(size); let at = 0;
  for (const chunk of chunks) { bytes.set(chunk, at); at += chunk.length; }
  return new TextDecoder('utf-8', {fatal:true}).decode(bytes);
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    // Intentionally no fetch(), origin, URL proxy, RPC, or crawler trigger.
    if (url.pathname === '/catalog.json' && ['GET','HEAD'].includes(request.method)) {
      const snapshot = await env.CATALOG.getWithMetadata('catalog', {type:'text', cacheTtl:60});
      if (!snapshot.value) return new Response('Not published yet', {status:503});
      const headers = {'Content-Type':'application/json; charset=utf-8', 'Cache-Control':'public, max-age=300',
        'ETag':`"${snapshot.metadata?.sha256}"`, 'X-Content-Type-Options':'nosniff'};
      if (request.headers.get('If-None-Match') === headers.ETag) return new Response(null, {status:304, headers});
      return new Response(request.method === 'HEAD' ? null : snapshot.value, {headers});
    }
    if (url.pathname === '/publish' && request.method === 'PUT') {
      if (!await authorized(request, env.PUBLISH_TOKEN)) return new Response('Unauthorized', {status:401});
      try {
        const body = await boundedBody(request);
        const data = JSON.parse(body);
        if (data.schema !== 1 || data.interval_hours !== 12 || !Array.isArray(data.items) || data.items.length > 30000 || !Number.isFinite(Date.parse(data.generated_at))) throw new Error('schema');
        for (const p of data.items) {
          if (!/^\d{1,20}$/.test(p.id) || p.url !== `https://booth.pm/ja/items/${p.id}` || typeof p.title !== 'string' || !Array.isArray(p.variants)) throw new Error('item');
        }
        const digest = await crypto.subtle.digest('SHA-256', enc.encode(body));
        const sha256 = [...new Uint8Array(digest)].map(b=>b.toString(16).padStart(2,'0')).join('');
        await env.CATALOG.put('catalog', body, {metadata:{sha256}});
        console.log(JSON.stringify({event:'published', items:data.items.length}));
        return Response.json({ok:true, items:data.items.length});
      } catch { return new Response('Invalid snapshot', {status:400}); }
    }
    return new Response('Not found', {status:404});
  }
};
