import test from 'node:test';
import assert from 'node:assert/strict';
import {streamToWindows} from '../extension/direct.mjs';
function response(payload,options={}){
 const data=new TextEncoder().encode(payload);
 return {ok:true,url:'https://booth.pximg.net/file',headers:new Headers({'Content-Type':'application/zip','Content-Length':String(data.length),...options.headers}),body:new ReadableStream({start(c){c.enqueue(data);c.close();}}),...options};
}
function transport(){const calls=[];let bytes=0;return {calls,async request(command,data){calls.push({command,...data});if(command==='file_chunk'){bytes+=Buffer.from(data.data,'base64').length;return {received:bytes};}return {path:'E:/manager/b1/fixture.zip',size:bytes};}};}
test('paired streaming writes only to Windows with bounded ordered chunks',async()=>{
 const t=transport(),payload='a'.repeat(700000),r=await streamToWindows(response(payload),'ticket',t);
 assert.equal(r.size,700000);assert.equal(t.calls[0].command,'begin_file');assert.equal(t.calls.at(-1).command,'finish_file');
 const chunks=t.calls.filter(c=>c.command==='file_chunk');assert.ok(chunks.length>1);assert.ok(chunks.every(c=>Buffer.from(c.data,'base64').length<=192*1024));
 assert.equal(chunks.map(c=>Buffer.from(c.data,'base64').toString()).join(''),payload);
});
test('direct save rejects login HTML and untrusted final download URLs before creating a file',async()=>{
 for(const r of [response('html',{headers:new Headers({'Content-Type':'text/html'})}),response('x',{url:'https://evil.invalid/file'})]){
  const t=transport();await assert.rejects(streamToWindows(r,'t',t));assert.equal(t.calls.length,0);
 }
});
test('truncated files and native disconnect abort direct save',async()=>{
 const t=transport();await assert.rejects(streamToWindows(response('short',{headers:new Headers({'Content-Type':'application/zip','Content-Length':'99'})}),'t',t));assert.equal(t.calls.at(-1).command,'abort_file');
 const failed=transport(),send=failed.request;failed.request=async(c,d)=>{if(c==='file_chunk')throw Error('disconnected');return send(c,d);};
 await assert.rejects(streamToWindows(response('bytes'),'t',failed),/disconnected/);assert.equal(failed.calls.at(-1).command,'abort_file');
});
