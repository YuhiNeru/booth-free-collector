import test from 'node:test';
import assert from 'node:assert/strict';
import {defaults,initialState,applyFeed,matches} from '../extension/core.mjs';
const item=category=>({id:'1',url:'https://booth.pm/ja/items/1',title:'衣装 髪型',shop:'shop',tags:[],categories:[category],age_rating:'all_ages',variants:[{id:'2'}]});
test('extension first download and recurring downloads are high priority only',()=>{
 assert.equal(defaults().includeExisting,true);
 for(const category of ['3D衣装','3D髪型','3Dテクスチャ','3D装飾品','3D小道具','3Dモーション・アニメーション','3D環境・ワールド','3Dツール・システム','3Dモデル','ソフトウェア']) {
  const expected=['3D衣装','3D髪型'].includes(category),p=item(category);
  assert.equal(matches(p,defaults()),expected,category);
  const feed={schema:1,interval_hours:12,generated_at:new Date().toISOString(),items:[p]};
  const first=initialState();applyFeed(first,feed,defaults());assert.equal(first.queue.length,expected?1:0);
  const next={...initialState(),baseline:true};applyFeed(next,feed,defaults());assert.equal(next.queue.length,expected?1:0);
 }
 assert.equal(matches(item('3D衣装'),{...defaults(),category:'3Dツール'}),true,'obsolete category setting cannot override fixed priorities');
 assert.equal(matches({...item('3D髪型'),age_rating:'adult'},defaults()),false);
});
