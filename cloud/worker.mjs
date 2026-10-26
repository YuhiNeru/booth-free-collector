const LIMIT = 12 * 1024 * 1024;
const enc = new TextEncoder();

function publicSnapshot(data) {
  const keys=(value,allowed)=>value&&typeof value==='object'&&!Array.isArray(value)&&Object.keys(value).every(k=>allowed.includes(k));
  const id=value=>typeof value==='string'&&/^[1-9]\d{0,19}$/.test(value);
  const text=(value,max)=>typeof value==='string'&&value.length<=max;
  const list=(value,max,length)=>Array.isArray(value)&&value.length<=max&&value.every(v=>text(v,length));
  const date=value=>typeof value==='string'&&value.length<=64&&Number.isFinite(Date.parse(value));
  if(!keys(data,['schema','interval_hours','generated_at','crawler_finished_at','crawler_status','items'])||
    data.schema!==1||data.interval_hours!==12||!date(data.generated_at)||!Array.isArray(data.items)||data.items.length>30000||
    data.crawler_finished_at!=null&&!date(data.crawler_finished_at)||
    data.crawler_status!=null&&!text(data.crawler_status,64))throw Error('schema');
  const ids=new Set();
  for(const p of data.items){
    if(!keys(p,['id','url','title','shop','categories','tags','variants','age_rating','free_kind','first_seen','fetched_at'])||
      !id(p.id)||ids.has(p.id)||p.url!==`https://booth.pm/ja/items/${p.id}`||!text(p.title,4000)||
      p.shop!=null&&!text(p.shop,1000)||p.categories!=null&&!list(p.categories,50,300)||p.tags!=null&&!list(p.tags,300,1000)||
      p.age_rating!=null&&!text(p.age_rating,64)||p.free_kind!=null&&!text(p.free_kind,100)||
      p.first_seen!=null&&!date(p.first_seen)||p.fetched_at!=null&&!date(p.fetched_at)||
      !Array.isArray(p.variants)||!p.variants.length||p.variants.length>1000)throw Error('item');
    ids.add(p.id);const variants=new Set();
    for(const v of p.variants){
      if(!keys(v,['id','name'])||!id(v.id)||variants.has(v.id)||v.name!=null&&!text(v.name,4000))throw Error('variant');
      variants.add(v.id);
    }
  }
}

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
      const generated=snapshot.metadata?.generated_at||JSON.parse(snapshot.value).generated_at;
      if(Number.isFinite(Date.parse(generated)))headers['X-Catalog-Generated-At']=new Date(generated).toISOString();
      if (request.headers.get('If-None-Match') === headers.ETag) return new Response(null, {status:304, headers});
      return new Response(request.method === 'HEAD' ? null : snapshot.value, {headers});
    }
    if (url.pathname === '/publish' && request.method === 'PUT') {
      if (!await authorized(request, env.PUBLISH_TOKEN)) return new Response('Unauthorized', {status:401});
      try {
        const body = await boundedBody(request);
        const data = JSON.parse(body);
        publicSnapshot(data);
        const digest = await crypto.subtle.digest('SHA-256', enc.encode(body));
        const sha256 = [...new Uint8Array(digest)].map(b=>b.toString(16).padStart(2,'0')).join('');
        await env.CATALOG.put('catalog', body, {metadata:{sha256,generated_at:data.generated_at}});
        console.log(JSON.stringify({event:'published', items:data.items.length}));
        return Response.json({ok:true, items:data.items.length});
      } catch { return new Response('Invalid snapshot', {status:400}); }
    }
    return new Response('Not found', {status:404});
  }
};
