import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import * as core from '../extension/core.mjs';

const product={id:'1',url:'https://booth.pm/ja/items/1',title:'Fixture',shop:'Shop',tags:[],categories:['3D衣装'],age_rating:'all_ages',variants:[{id:'2'}]};
function fixture({missing=false,disconnect=false}={}) {
  const generated=new Date().toISOString(),calls=[];
  const data={state:{...core.initialState(),baseline:true,generatedAt:generated,lastAttempt:Date.now(),queue:[product]},settings:{...core.defaults(),enabled:true},catalog:{schema:1,interval_hours:12,generated_at:generated,items:[product]}};
  if(missing)data.state.done['1/10']={key:'1/10',productId:'1',fileId:'10',variantId:'2',name:'fixture.txt',url:'https://booth.pm/downloadables/10?variation_id=2',downloadId:10,path:'C:/old/fixture.txt',imported:false};
  const event={addListener(){}};
  const chrome={storage:{local:{async get(){return structuredClone(data);},async set(value){Object.assign(data,structuredClone(value));}}},runtime:{onInstalled:event,onStartup:event,onMessage:event,id:'test',async sendNativeMessage(host,message){calls.push(message);switch(message.command){case 'status':return {desktop:true,configured:true,last_attempt:new Date(data.state.lastAttempt).toISOString(),generated_at:generated};case 'sync_pending':return {desktop:true,ok:true};case 'jobs':return {desktop:true,ok:true,auto_download:false,items:[]};case 'prepare':return {desktop:true,ok:true,ticket:'test'};case 'complete':return {desktop:true,ok:false,error:'取得済みファイルが見つかりません'};default:throw Error(message.command);}}},downloads:{onChanged:event,async search(){return [];}},alarms:{onAlarm:event,async create(){}},action:{async setBadgeText(){},async setBadgeBackgroundColor(){}},notifications:{async create(){}}};
  if(disconnect){const send=chrome.runtime.sendNativeMessage;chrome.runtime.sendNativeMessage=(host,message)=>message.command==='sync_pending'?Promise.resolve({desktop:false}):send(host,message);}
  let pendingIds=[];
  const nativeSend=chrome.runtime.sendNativeMessage;
  chrome.runtime.sendNativeMessage=(host,message)=>message.command==='pending'?Promise.resolve({desktop:true,ok:true,product_ids:pendingIds}):nativeSend(host,message);
  const context=vm.createContext({...core,chrome,URL,AbortSignal,TextDecoder,Date,console,fetch(){throw Error('unexpected network');}});
  const source=fs.readFileSync(new URL('../extension/background.mjs',import.meta.url),'utf8').replace(/^import .*?;\r?\n/gm,'');
  vm.runInContext(source,context);
  const send=chrome.runtime.sendNativeMessage;
  return {data,calls,tick:(force=true)=>vm.runInContext(`tick(${force})`,context),setPending(ids){pendingIds=ids;},setDesktop(value){chrome.runtime.sendNativeMessage=(host,message)=>!value?Promise.resolve({desktop:false}):send(host,message);}};
}
test('solo pending products are transferred before paired jobs, without fetching again',async()=>{
  const f=fixture();await f.tick();
  assert.deepEqual(f.calls.find(c=>c.command==='sync_pending')?.product_ids,['1']);
  assert.ok(f.calls.findIndex(c=>c.command==='sync_pending')<f.calls.findIndex(c=>c.command==='jobs'));
  assert.equal(f.data.state.queue.length,0);
  await f.tick();assert.equal(f.calls.filter(c=>c.command==='sync_pending').length,1);
});
test('disconnect during takeover retains the solo queue until acknowledgement',async()=>{
  const f=fixture({disconnect:true});await f.tick();
  assert.equal(f.data.state.queue.length,1);
  assert.match(f.data.state.blocked,/引継ぎ/);
});
test('a missing old browser file does not block the current paired queue',async()=>{
  const f=fixture({missing:true});await f.tick();
  assert.equal(f.data.state.blocked,'');
  assert.ok(f.calls.some(c=>c.command==='jobs'));
  assert.ok(f.data.state.done['1/10'].importError);
});
test('acknowledged but unfinished jobs survive Windows shutdown in persisted state',async()=>{
  const f=fixture();f.setDesktop(true);f.data.settings.auto=false;
  await f.tick();assert.equal(f.data.state.queue.length,0);
  f.setDesktop(false);await f.tick(false);
  assert.equal(f.data.state.paired,false);
  assert.equal(f.data.state.queue[0]?.id,'1');
});
test('Windows-only pending work is mirrored beyond the ten-job processing page',async()=>{
  const f=fixture();f.data.settings.auto=false;f.data.state.queue=[];
  const products=Array.from({length:15},(_,i)=>({...product,id:String(i+1),url:`https://booth.pm/ja/items/${i+1}`}));
  f.data.catalog.items=products;f.setPending(products.map(p=>p.id));
  await f.tick();f.setDesktop(false);await f.tick(false);
  assert.equal(f.data.state.queue.length,15);
});
test('authentication blocks remain blocked when Windows disconnects',async()=>{
  const f=fixture();f.data.settings.auto=false;f.data.state.blocked='ログインが必要です';
  f.setDesktop(false);await f.tick(false);
  assert.equal(f.data.state.blocked,'ログインが必要です');
});
test('transient bridge disconnect clears automatically on solo fallback, without manual resume',async()=>{
  const f=fixture({disconnect:true});f.setDesktop(true);f.data.settings.auto=false;
  await f.tick();assert.ok(f.data.state.blocked);
  f.setDesktop(false);await f.tick(false);
  assert.equal(f.data.state.blocked,'');
  assert.equal(f.data.state.queue[0]?.id,'1');
});
