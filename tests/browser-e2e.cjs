/* Isolated Chromium profile only. Never reads the user's browser profile. */
const {chromium}=require('playwright-core');
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const https=require('node:https'),http=require('node:http'),net=require('node:net');
const {spawn,execFileSync}=require('node:child_process');
const root=path.resolve(process.argv[2]||'state/browser-v2-test');fs.mkdirSync(root,{recursive:true});
const paired=process.argv.includes('--paired');
const midway=process.argv.includes('--disconnect-midway');
const exe=path.resolve('BoothFreeCollector/bin/Debug/net10.0-windows/BoothFreeCollector.exe');
let app,extensionPath=path.resolve('extension');
const p={id:'1',url:'https://booth.pm/ja/items/1',title:'Fixture file',shop:'Fixture',tags:[],categories:['3D衣装'],age_rating:'all_ages',variants:[{id:'2',name:'Free'}]};
const feed={schema:1,interval_hours:12,generated_at:new Date().toISOString(),items:[p]};
(async()=>{
 if(paired){
  const desktop=path.join(root,'desktop');fs.mkdirSync(desktop,{recursive:true});
  fs.writeFileSync(path.join(desktop,'settings.json'),JSON.stringify({configured:true,follow_library_folder:false,download_folder:path.join(root,'library'),auto_download:true,include_existing:true,feed_url:'https://127.0.0.1/catalog.json'}));
  fs.writeFileSync(path.join(desktop,'state.json'),JSON.stringify({last_attempt:null,last_success:new Date().toISOString(),baseline_created:true,generated_at:feed.generated_at,seen:['1'],known_variants:{'1':['2']},queue:[],files:{}}));
  fs.writeFileSync(path.join(desktop,'catalog.json'),JSON.stringify(feed));
  extensionPath=path.join(root,'extension');fs.cpSync('extension',extensionPath,{recursive:true});
  const bg=path.join(extensionPath,'background.mjs');fs.writeFileSync(bg,fs.readFileSync(bg,'utf8').replace("const HOST='pm.booth.free_collector';","const HOST='pm.booth.free_collector_test';"));
  const manifest=path.join(root,'native-host.json');fs.writeFileSync(manifest,JSON.stringify({name:'pm.booth.free_collector_test',description:'Isolated integration test only',path:exe,type:'stdio',allowed_origins:['chrome-extension://ipocnbpbmnbjhliliadogibolpcmnjhd/']}));
  execFileSync('reg.exe',['add','HKCU\\Software\\Google\\Chrome\\NativeMessagingHosts\\pm.booth.free_collector_test','/ve','/t','REG_SZ','/d',manifest,'/f'],{stdio:'ignore'});
  app=spawn(exe,['--tray'],{env:{...process.env,BOOTH_COLLECTOR_TEST_STATE:desktop},windowsHide:true,stdio:'ignore'});
  await new Promise(r=>setTimeout(r,1500));
  assert.equal(JSON.parse(fs.readFileSync(path.join(desktop,'state.json'),'utf8')).last_attempt,null,'desktop waits for extension schedule before fetching');
 }
 let feedCalls=0,fileCalls=0,authenticatedDownloads=0;
 const tls=https.createServer({key:fs.readFileSync('state/test-tls-key.pem'),cert:fs.readFileSync('state/test-tls-cert.pem')},(req,res)=>{
  if(req.headers.host?.includes('workers.dev')&&req.url==='/catalog.json'){feedCalls++;res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify(feed));return;}
  if(['/ja/items/1','/ja/items/2'].includes(req.url)){const file=req.url.endsWith('/2')?'20':'10';res.writeHead(200,{'Content-Type':'text/html'});res.end(`<a href="https://booth.pm/downloadables/${file}?variation_id=2" title="fixture.txt">無料ダウンロード</a>`);return;}
  if(['/downloadables/10?variation_id=2','/downloadables/20?variation_id=2'].includes(req.url)){
   fileCalls++;
   if(!req.headers.cookie?.includes('fixture-session=browser-only')){res.writeHead(302,{Location:'https://booth.pm/users/sign_in'});res.end();return;}
   if(req.method==='GET')authenticatedDownloads++;
   res.writeHead(200,{'content-type':'application/octet-stream','content-disposition':'attachment; filename=fixture.txt'});
   if(midway&&req.method==='GET'){res.flushHeaders();setTimeout(()=>res.end('extension fixture payload'),3000);}else res.end(req.method==='HEAD'?undefined:'extension fixture payload');return;
  }
  res.writeHead(200,{'Content-Type':'text/html'});res.end('<p>Fixture sign in</p>');
 });
 await new Promise(r=>tls.listen(0,'127.0.0.1',r));
 const proxy=http.createServer((req,res)=>{res.writeHead(204);res.end();});
 proxy.on('connect',(req,socket,head)=>{const upstream=net.connect(tls.address().port,'127.0.0.1',()=>{socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');if(head.length)upstream.write(head);socket.pipe(upstream);upstream.pipe(socket);});upstream.on('error',()=>socket.destroy());socket.on('error',()=>upstream.destroy());});
 await new Promise(r=>proxy.listen(0,'127.0.0.1',r));
 fs.mkdirSync(path.join(root,'profile','Default'),{recursive:true});fs.mkdirSync(path.join(root,'downloads'),{recursive:true});
 fs.writeFileSync(path.join(root,'profile','Default','Preferences'),JSON.stringify({download:{default_directory:path.join(root,'downloads'),prompt_for_download:false}}));
 const context=await chromium.launchPersistentContext(path.join(root,'profile'),{executablePath:'C:/Users/dayu/AppData/Local/ms-playwright/chromium-1234/chrome-win64/chrome.exe',headless:true,env:{...process.env,...(paired?{BOOTH_COLLECTOR_TEST_STATE:path.join(root,'desktop')}:{})},proxy:{server:`http://127.0.0.1:${proxy.address().port}`},args:['--ignore-certificate-errors',`--disable-extensions-except=${extensionPath}`,`--load-extension=${extensionPath}`]});
 try{
  let worker=context.serviceWorkers()[0]||await context.waitForEvent('serviceworker');
  const id=new URL(worker.url()).host;assert.equal(id,'ipocnbpbmnbjhliliadogibolpcmnjhd');
  const page=await context.newPage();await page.goto(`chrome-extension://${id}/options.html`);
  const cdp=await context.newCDPSession(page);await cdp.send('Browser.setDownloadBehavior',{behavior:'default'});
  await context.addCookies([{name:'fixture-session',value:'browser-only',domain:'booth.pm',path:'/',secure:true,sameSite:'Lax'}]);
  await worker.evaluate(async paired=>{await chrome.storage.local.set({settings:{enabled:true,auto:true,linkWindows:paired,includeAdult:false,includeExisting:!paired,search:'',category:''}});},paired);
  if(paired)await worker.evaluate(async()=>{await chrome.storage.local.set({state:{lastAttempt:Date.now()}});});
  await page.evaluate(()=>chrome.runtime.sendMessage({action:'resume'}));
  if(paired){
    // Reproduce solo -> paired takeover with a pending product and a stale
    // staging file. Chrome must append (1); native import must still succeed.
    const staging=path.join(root,'downloads','BOOTH-Free-Collector','b1','10');fs.mkdirSync(staging,{recursive:true});fs.writeFileSync(path.join(staging,'fixture.txt'),'unrelated older staged file');
    await worker.evaluate(async p=>{const {state}=await chrome.storage.local.get('state');state.queue=[p];await chrome.storage.local.set({state});},p);
    const result=await page.evaluate(()=>chrome.runtime.sendMessage({action:'resume'}));assert.equal(result.ok,true,result.error);
    if(midway){
      const active=await worker.evaluate(()=>chrome.storage.local.get('state'));
      assert.ok(active.state.active,'download must still be in progress');
      assert.ok(active.state.fallbackQueue.some(p=>p.id==='1'),'fallback is persisted before desktop shutdown');
      app.kill();await new Promise(r=>setTimeout(r,300));
    }
  }
  let data;
  for(let i=0;i<40;i++){
   data=await worker.evaluate(()=>chrome.storage.local.get('state'));
   if(Object.keys(data.state?.done||{}).length||data.state?.blocked)break;
   await new Promise(r=>setTimeout(r,500));
  }
  fs.writeFileSync(path.join(root,'observed.json'),JSON.stringify({data,feedCalls,fileCalls},null,2));
  assert.equal(data.state.blocked,'');assert.ok(data.state.done['1/10']);assert.equal(feedCalls,paired?0:1);assert.equal(authenticatedDownloads,1);
  assert.equal(fs.readFileSync(data.state.done['1/10'].path,'utf8'),'extension fixture payload');
  if(paired&&!midway){assert.equal(await worker.evaluate(async()=> (await chrome.downloads.search({})).length),0,'paired direct save must not create a browser download');}
  if(paired){assert.equal(data.state.done['1/10'].imported,!midway);if(!midway)assert.equal(data.state.done['1/10'].path,path.join(root,'library','b1','fixture.txt'));assert.equal(fs.readFileSync(path.join(root,'downloads','BOOTH-Free-Collector','b1','10','fixture.txt'),'utf8'),'unrelated older staged file');}
  await new Promise(r=>setTimeout(r,5100));
  await page.evaluate(()=>chrome.runtime.sendMessage({action:'resume'}));assert.equal(feedCalls,paired?0:1);assert.equal(authenticatedDownloads,1);
  if(midway){
    const restored=await worker.evaluate(()=>chrome.storage.local.get('state'));assert.equal(restored.state.paired,false);assert.equal(restored.state.queue.length,0);assert.equal(restored.state.blocked,'');
    app=spawn(exe,['--tray'],{env:{...process.env,BOOTH_COLLECTOR_TEST_STATE:path.join(root,'desktop')},windowsHide:true,stdio:'ignore'});
    await new Promise(r=>setTimeout(r,1500));
    await page.evaluate(()=>chrome.runtime.sendMessage({action:'resume'}));
    const reconnected=await worker.evaluate(()=>chrome.storage.local.get('state'));
    assert.equal(reconnected.state.done['1/10'].imported,true);
    assert.equal(reconnected.state.done['1/10'].path,path.join(root,'library','b1','fixture.txt'));
    assert.equal(authenticatedDownloads,1);assert.equal(feedCalls,0);
  }
  if(paired){
    const desktopText=fs.readFileSync(path.join(root,'desktop','state.json'),'utf8');
    assert.ok(!desktopText.includes('browser-only')&&!desktopText.includes('fixture-session'));
    app.kill();await new Promise(r=>setTimeout(r,300));
    await page.evaluate(()=>chrome.runtime.sendMessage({action:'resume'}));
    const disconnected=await worker.evaluate(()=>chrome.storage.local.get('state'));
    assert.equal(disconnected.state.paired,false);assert.equal(disconnected.state.blocked,'');assert.equal(authenticatedDownloads,1);
  }else{
    await context.clearCookies();
    await worker.evaluate(async()=>{const data=await chrome.storage.local.get('state');data.state.queue=[{id:'2',url:'https://booth.pm/ja/items/2',title:'Auth-required fixture',shop:'Fixture',tags:[],categories:['3D衣装'],age_rating:'all_ages',variants:[{id:'2'}]}];data.state.nextActionAt=0;await chrome.storage.local.set(data);});
    await page.evaluate(()=>chrome.runtime.sendMessage({action:'resume'}));
    const blocked=await worker.evaluate(()=>chrome.storage.local.get('state'));assert.match(blocked.state.blocked,/ログイン/);assert.equal(authenticatedDownloads,1);
  }
  await page.reload();
  await page.screenshot({path:path.join(root,'extension.png'),fullPage:true});
  fs.writeFileSync(path.join(root,'result.json'),JSON.stringify({ok:true,paired,feedCalls,fileCalls,passed:['isolated Chromium extension loaded',midway?'active download survives desktop shutdown':paired?'real Native Messaging direct stream into Windows folder':'standalone browser-only authenticated download',...(paired?['desktop startup waits for extension schedule','solo pending queue transferred without feed HTTP',midway?'unfinished job restored to solo without duplicate':'direct manager save without browser download; previous staged file preserved','desktop shutdown falls back without duplicate']:['expired authentication pauses download']),'12-hour throttle persists on manual resume','no BOOTH tab opened','no duplicate download'],pages:context.pages().map(p=>p.url())},null,2));
 }finally{await context.close();proxy.closeAllConnections();proxy.close();tls.closeAllConnections();tls.close();}
})().catch(error=>{fs.writeFileSync(path.join(root,'error.txt'),error.stack);process.exitCode=1;}).finally(()=>{if(app){app.kill();execFileSync('reg.exe',['delete','HKCU\\Software\\Google\\Chrome\\NativeMessagingHosts\\pm.booth.free_collector_test','/f'],{stdio:'ignore'});}});
