import test from 'node:test';
import assert from 'node:assert/strict';
import {initialState,defaults,applyFeed,validateFeed,validateLink,safeName,INTERVAL} from '../extension/core.mjs';
const product=()=>({id:'1',url:'https://booth.pm/ja/items/1',title:'試験 Unity',shop:'shop',tags:[],categories:['3D衣装'],age_rating:'all_ages',variants:[{id:'2',name:'free'}]});
const feed=items=>({schema:1,interval_hours:12,generated_at:new Date().toISOString(),items});
test('baseline then new variant, and no duplicate queue',()=>{
 const state=initialState(),p=product();applyFeed(state,feed([p]),{...defaults(),includeExisting:false});assert.equal(state.queue.length,0);
 p.variants.push({id:'3'});applyFeed(state,feed([p]),defaults());assert.equal(state.queue.length,1);
 applyFeed(state,feed([p]),defaults());assert.equal(state.queue.length,1);
 applyFeed(state,feed([]),defaults());assert.equal(state.queue.length,0);
});
test('existing items opt-in and filter',()=>{
 const s={...defaults(),includeExisting:true,search:'Unity',category:'3D'};
 const state=initialState();applyFeed(state,feed([product()]),s);assert.equal(state.queue.length,1);
 const adult=product();adult.age_rating='adult';const other=initialState();applyFeed(other,feed([adult]),s);assert.equal(other.queue.length,0);
});
test('strict free URL, known variant, and filename checks',()=>{
 assert.equal(validateLink(product(),{url:'https://booth.pm/downloadables/10?variation_id=2',name:'a.zip'}).key,'1/10');
 assert.throws(()=>validateLink(product(),{url:'https://booth.pm/downloadables/10?variation_id=999',name:'a.zip'}));
 assert.throws(()=>validateLink(product(),{url:'https://evil.invalid/downloadables/10?variation_id=2',name:'a.zip'}));
 for(const name of ['../x','CON.zip','a:b','abc.','a\\b'])assert.throws(()=>safeName(name));
});
test('feed rejects proxy URLs, duplicate ids, and wrong interval',()=>{
 const p=product();p.url='http://127.0.0.1/';assert.throws(()=>validateFeed(feed([p])));
 assert.throws(()=>validateFeed(feed([product(),product()])));
 assert.throws(()=>validateFeed({...feed([]),interval_hours:1}));
 assert.equal(INTERVAL,43200000);
});
