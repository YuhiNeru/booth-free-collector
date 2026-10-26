import test from 'node:test';
import assert from 'node:assert/strict';
import worker from '../cloud/worker.mjs';

test('public endpoint only reads KV, never contacts an origin', async () => {
  let reads = 0;
  const env = {CATALOG:{getWithMetadata:async()=>{reads++; return {value:'{"schema":1}',metadata:{sha256:'abc'}};}}};
  const r=await worker.fetch(new Request('https://example.test/catalog.json?url=http://127.0.0.1'),env);
  assert.equal(r.status,200); assert.equal(reads,1); assert.equal(await r.text(),'{"schema":1}');
  const cached=await worker.fetch(new Request('https://example.test/catalog.json',{headers:{'If-None-Match':'"abc"'}}),env);
  assert.equal(cached.status,304);
  const bad=await worker.fetch(new Request('https://example.test/run-crawler'),env); assert.equal(bad.status,404);
});

test('HEAD exposes publication time without a catalog body or crawler request',async()=>{
 let reads=0;
 const env={CATALOG:{getWithMetadata:async()=>{reads++;return {value:JSON.stringify({generated_at:'2026-10-26T00:00:00.123456Z'}),metadata:{sha256:'fixture'}};}}};
 const response=await worker.fetch(new Request('https://fixture.invalid/catalog.json',{method:'HEAD'}),env);
 assert.equal(response.status,200);assert.equal(response.headers.get('X-Catalog-Generated-At'),'2026-10-26T00:00:00.123Z');
 assert.equal(await response.text(),'');assert.equal(reads,1);
});
test('publishing requires secret and validates bounded schema',async()=>{
  let writes=0;
  const env={PUBLISH_TOKEN:'x'.repeat(48),CATALOG:{put:async()=>writes++}};
  const req=(body,token)=>new Request('https://example.test/publish',{method:'PUT',headers:{Authorization:token||''},body});
  assert.equal((await worker.fetch(req('{}'),env)).status,401);
  assert.equal((await worker.fetch(req('{}','Bearer '+env.PUBLISH_TOKEN),env)).status,400);
  const good={schema:1,interval_hours:12,generated_at:'2026-10-25T09:00:00Z',items:[{id:'1',title:'test',url:'https://booth.pm/ja/items/1',variants:[{id:'2'}]}]};
  assert.equal((await worker.fetch(req(JSON.stringify(good),'Bearer '+env.PUBLISH_TOKEN),env)).status,200);
  assert.equal(writes,1);
  for(const mutate of [
    s=>s.cookies='fixture',s=>s.items[0].download_url='https://booth.pm/downloadables/10',
    s=>s.items[0].variants[0].token='fixture',s=>s.items.push(structuredClone(s.items[0])),
    s=>s.items[0].variants[0].id='invalid',s=>s.items[0].title='x'.repeat(4001)
  ]){
    const unsafe=structuredClone(good);mutate(unsafe);
    assert.equal((await worker.fetch(req(JSON.stringify(unsafe),'Bearer '+env.PUBLISH_TOKEN),env)).status,400);
    assert.equal(writes,1);
  }
  good.items[0].url='http://127.0.0.1/';
  assert.equal((await worker.fetch(req(JSON.stringify(good),'Bearer '+env.PUBLISH_TOKEN),env)).status,400);
  assert.equal(writes,1);
  assert.equal((await worker.fetch(req('x'.repeat(12*1024*1024+1),'Bearer '+env.PUBLISH_TOKEN),env)).status,400);
});
