import {FEED_URL,INTERVAL,initialState,defaults,validateFeed,applyFeed,matches,validateLink,highPriority} from './core.mjs';
import {transferPort,streamToWindows} from './direct.mjs';
const HOST='pm.booth.free_collector';
class BridgeDisconnected extends Error {}
let chain=Promise.resolve(), offscreenCreation;
const serial=fn=>{const result=chain.then(fn);chain=result.catch(()=>{});return result;};
const read=async()=>{const data=await chrome.storage.local.get(['state','settings','catalog']);return {state:{...initialState(),...data.state},settings:{...defaults(),...data.settings},catalog:data.catalog};};
const save=async state=>chrome.storage.local.set({state});
async function native(command,body={}) {
  const config=await chrome.storage.local.get('settings');
  if(config.settings?.linkWindows===false)return {desktop:false};
  try { return await chrome.runtime.sendNativeMessage(HOST,{command,...body}); }
  catch { return {desktop:false}; }
}
async function bounded(response,limit) {
  if(!response.ok) throw Error(`通信エラー HTTP ${response.status}`);
  const reader=response.body.getReader(), chunks=[];let total=0;
  try { while(true) {const {done,value}=await reader.read();if(done)break;total+=value.length;if(total>limit)throw Error('応答の容量上限です');chunks.push(value);} }
  finally {await reader.cancel();}
  const data=new Uint8Array(total);let pos=0;for(const c of chunks){data.set(c,pos);pos+=c.length;}
  return new TextDecoder('utf-8',{fatal:true}).decode(data);
}
async function parser(html) {
  const contexts=await chrome.runtime.getContexts({contextTypes:['OFFSCREEN_DOCUMENT'],documentUrls:[chrome.runtime.getURL('offscreen.html')]});
  if(!contexts.length) {
    offscreenCreation??=chrome.offscreen.createDocument({url:'offscreen.html',reasons:['DOM_PARSER'],justification:'無料ダウンロードリンクを画面を開かずに解析する'}).finally(()=>{offscreenCreation=null;});
    await offscreenCreation;
  }
  const result=await chrome.runtime.sendMessage({target:'parser',html});
  if(result.error)throw Error(result.error);return result.links;
}
async function fetchFeed(state,settings,lease) {
  const headers={};if(state.etag)headers['If-None-Match']=state.etag;
  const response=await fetch(FEED_URL,{headers,credentials:'omit',redirect:'error',signal:AbortSignal.timeout(25000)});
  if(response.status===304) {
    state.lastSuccess=Date.now();state.status='一覧に更新はありません';
    if(lease)await native('feed',{lease,not_modified:true});return;
  }
  const feed=validateFeed(JSON.parse(await bounded(response,12*1024*1024)));
  applyFeed(state,feed,settings);state.etag=response.headers.get('ETag');
  await chrome.storage.local.set({catalog:feed});
  if(lease){const result=await native('feed',{lease,feed,etag:state.etag||''});if(!result.desktop)throw new BridgeDisconnected('Windowsへの一覧反映を待っています');if(!result.ok)throw Error(result.error||'Windowsへの一覧反映に失敗しました');}
}
async function synchronizeCatalog(state,settings,status) {
  if(!status.generated_at||status.generated_at===state.generatedAt)return;
  const all=[];let total=Infinity,generated;
  while(all.length<total) {
    const page=await native('catalog',{offset:all.length});
    if(!page.desktop)throw new BridgeDisconnected('Windowsとの同期を待っています');
    if(!page.ok)throw Error('Windowsとの同期を待っています');
    total=page.total;generated=page.generated_at;
    if(!page.items.length)break;all.push(...page.items);
  }
  if(generated&&all.length===total){const feed={schema:1,interval_hours:12,generated_at:generated,items:all};applyFeed(state,feed,settings);await chrome.storage.local.set({catalog:feed});}
}
async function notify(message) {
  await chrome.action.setBadgeText({text:'!'});await chrome.action.setBadgeBackgroundColor({color:'#c24a5f'});
  // Notification has no URL, credential, or file path.
  await chrome.notifications.create('attention',{type:'basic',iconUrl:'icon.png',title:'BOOTH Free Collector',message});
}
async function checkAuth(url) {
  // The browser attaches credentials. They are never read or copied to native code.
  let response=await fetch(url,{method:'HEAD',credentials:'include',redirect:'follow',signal:AbortSignal.timeout(25000)});
  if(response.status===405){response=await fetch(url,{headers:{Range:'bytes=0-0'},credentials:'include',redirect:'follow',signal:AbortSignal.timeout(25000)});}
  const login=/\/(?:users\/sign_in|sign_in|login)(?:[/?]|$)/.test(response.url)||new URL(response.url).hostname==='accounts.pixiv.net';
  const html=(response.headers.get('Content-Type')||'').includes('text/html');
  if(response.body)await response.body.cancel();
  if(login||html||response.status===401||response.status===403)throw Error('BOOTHへのログイン・アクセス確認が必要です。普段のブラウザで確認後「再開」を押してください');
  if(!response.ok)throw Error(`ダウンロードを確認できません HTTP ${response.status}`);
}
async function knownDownload(state,link) {
  const record=state.done[link.key];
  if(record?.imported)return record;
  if(record?.downloadId){const found=await chrome.downloads.search({id:record.downloadId});if(found[0]?.state==='complete'&&found[0]?.exists)return {...record,path:found[0].filename};}
  // A previous Windows handoff may already be in this browser's download history.
  const found=await chrome.downloads.search({url:link.url,state:'complete',exists:true,limit:10,orderBy:['-startTime']});
  const item=found.find(d=>d.mime!=='text/html');
  return item?{...link,downloadId:item.id,path:item.filename,imported:false}:null;
}
async function importCompleted(state,record,ticket) {
  if(record.imported)return true;
  let preparation=ticket?{ok:true,ticket}:await native('prepare',{product_id:record.productId,file_id:record.fileId,variant_id:record.variantId,name:record.name});
  if(!preparation.desktop&&!ticket)return false;
  if(preparation.skip){record.imported=true;state.done[record.key]=record;return true;}
  if(!preparation.ok)return false;
  let result=await native('complete',{ticket:preparation.ticket,path:record.path});
  if(result.desktop&&!result.ok&&ticket)return importCompleted(state,record,null);
  if(result.ok){record.imported=true;record.path=result.path;state.done[record.key]=record;return true;}
  if(result.desktop)throw Error(result.error||'保存先への整理を待っています');
  return false;
}
async function reconcileCompleted(state) {
  // An old file may have been moved/deleted by the user or Library Manager.
  // Keep its history and retry independently; it must not stop new downloads.
  const pending=Object.values(state.done).filter(r=>!r.imported&&Date.now()>=(r.importRetryAt||0)).slice(0,10);
  for(const record of pending) {
    try {
      if(!await importCompleted(state,record,null))continue;
      delete record.importError;delete record.importRetryAt;
    } catch(error) {
      record.importError=String(error.message||error);
      record.importRetryAt=Date.now()+30*60*1000;
    }
  }
  state.importWarnings=Object.values(state.done).filter(r=>!r.imported&&r.importError).length;
}
async function transferPending(state) {
  // Acknowledgement precedes removal, so a disconnect cannot lose solo jobs.
  const ids=state.queue.slice(0,100).map(p=>p.id);
  if(!ids.length)return;
  const result=await native('sync_pending',{product_ids:ids});
  if(!result.desktop)throw new BridgeDisconnected('Windowsへの待機分の引継ぎを待っています');
  if(!result.ok)throw Error(result.error||'Windowsへの待機分の引継ぎを待っています');
  state.fallbackQueue=[...new Map([...(state.fallbackQueue||[]),...state.queue.filter(p=>ids.includes(p.id))].map(p=>[p.id,p])).values()];
  state.queue=state.queue.filter(p=>!ids.includes(p.id));await save(state);
}
async function mirrorPending(state) {
  const result=await native('pending');
  if(!result.desktop)throw new BridgeDisconnected('Windowsの待機分を同期しています');
  if(!result.ok)throw Error(result.error||'Windowsの待機分を同期できません');
  const {catalog}=await chrome.storage.local.get('catalog');
  const ids=new Set(result.product_ids);
  state.fallbackQueue=(catalog?.items||[]).filter(p=>ids.has(p.id));
  await save(state);
}
async function processProduct(state,product,paired) {
  const response=await fetch(product.url,{credentials:'include',redirect:'follow',signal:AbortSignal.timeout(25000)});
  if(response.url.includes('/sign_in'))throw Error('普段のブラウザでBOOTHにログインしてから再開してください');
  const html=await bounded(response,2*1024*1024);
  if(/cf-chl-|verify you are human|<title>\s*just a moment/i.test(html))throw Error('BOOTHのアクセス確認が必要です。ブラウザで確認してから再開してください');
  const entries=await parser(html),links=[];
  for(const entry of entries){try{const link=validateLink(product,entry);if(!links.some(l=>l.key===link.key))links.push(link);}catch{}}
  if(!links.length)throw Error('無料リンクが見つかりません。販売条件やログイン状態を確認してください');
  for(const link of links) {
    let preparation;
      if(paired){preparation=await native('prepare',{product_id:link.productId,file_id:link.fileId,variant_id:link.variantId,name:link.name});if(!preparation.desktop)throw new BridgeDisconnected('Windowsとの接続が切れました。次の確認で単体モードへ切り替えます');if(!preparation.ok)throw Error(preparation.error||'Windows処理待ち');if(preparation.skip){state.done[link.key]={...state.done[link.key],...link,imported:true};continue;}}
    if(paired){
      if(!preparation.direct_stream)throw Error('直接保存に対応するWindows版へ更新してください');
      state.status=`Library Managerの保存先へ直接保存中: ${product.title}`;await save(state);
      const transport=transferPort(chrome,HOST);
      try{
        const file=await fetch(link.url,{credentials:'include',redirect:'follow',signal:AbortSignal.timeout(30*60*1000)});
        const result=await streamToWindows(file,preparation.ticket,transport);
        state.done[link.key]={...link,path:result.path,imported:true,size:result.size};
        state.status='Library Managerの保存先へ直接保存しました';await save(state);
      }finally{transport.close();}
      continue;
    }
    let known=await knownDownload(state,link);
    if(paired&&known?.imported){delete state.done[link.key];known=null;}
    if(known){state.done[link.key]=known;if(paired)await importCompleted(state,known,preparation?.ticket);continue;}
    await checkAuth(link.url);
    const filename=preparation?.relative_filename||`BOOTH-Free-Collector/b${link.productId}/${link.fileId}/${link.name}`;
    const downloadId=await chrome.downloads.download({url:link.url,filename,saveAs:false,conflictAction:'uniquify'});
    state.active={...link,downloadId,ticket:preparation?.ticket,paired};state.status=`取得中: ${product.title}`;
    await save(state);return;
  }
  if(paired){const result=await native('finish',{product_id:product.id});if(!result.desktop)throw new BridgeDisconnected('Windows側の処理完了を待っています');if(!result.ok)throw Error('Windows側の処理完了を待っています');}
  state.queue=state.queue.filter(p=>p.id!==product.id);
  state.fallbackQueue=(state.fallbackQueue||[]).filter(p=>p.id!==product.id);
  state.status='取得完了。次の新着確認まで待機しています';await save(state);
}
async function completeActive(state) {
  const active=state.active;if(!active)return;
  const list=await chrome.downloads.search({id:active.downloadId}),item=list[0];
  if(item&&(item.totalBytes>2*1024*1024*1024||item.bytesReceived>2*1024*1024*1024)){if(item.state==='in_progress')await chrome.downloads.cancel(item.id);state.active=null;throw Error('1ファイル2GiBの容量上限です');}
  if(!item||item.state==='interrupted'){state.active=null;throw Error(`ダウンロードが中断されました: ${item?.error||'履歴なし'}`);}
  if(item.state!=='complete')return;
  if(item.mime==='text/html'){state.active=null;throw Error('ファイルではなくログイン画面が返されました。BOOTHを確認してください');}
  const record={...active,path:item.filename,imported:false};delete record.ticket;delete record.paired;
  state.done[active.key]=record;
  if(active.paired)await importCompleted(state,record,active.ticket);
  state.active=null;state.nextActionAt=Date.now()+5000;state.status='ファイルを保存しました';await save(state);
}
async function tick(force=false) {
  const {state,settings}=await read();
  if(!settings.enabled)return;
  if(force)state.blocked='';
  try {
    const status=await native('status',{last_attempt:state.lastAttempt?new Date(state.lastAttempt).toISOString():''});
    state.paired=!!status.desktop;
    if(state.connectionBlocked){state.blocked='';state.connectionBlocked=false;}
    if(!state.paired&&state.fallbackQueue?.length){
      state.queue=[...new Map([...state.fallbackQueue,...state.queue].map(p=>[p.id,p])).values()];
      state.fallbackQueue=[];await save(state);
    }
    if(state.paired) {
      if(status.busy){state.status='Windows側の処理を待っています';await save(state);return;}
      if(!status.configured)throw Error('Windowsアプリで初期設定を保存してください');
      state.lastAttempt=Math.max(state.lastAttempt,Date.parse(status.last_attempt)||0);await save(state);
      if(state.generatedAt&&(!status.generated_at||Date.parse(state.generatedAt)>Date.parse(status.generated_at))){
        const cache=await chrome.storage.local.get('catalog');
        if(cache.catalog){const synced=await native('sync_cache',{feed:cache.catalog});if(!synced.desktop)throw new BridgeDisconnected('一覧の同期を待っています');if(!synced.ok)throw Error(synced.error||'一覧の同期を待っています');status.generated_at=state.generatedAt;}
      }
      await synchronizeCatalog(state,settings,status);
      if(Date.now()-state.lastAttempt>=INTERVAL) {
        const reservation=await native('reserve');
        state.lastAttempt=Math.max(state.lastAttempt,Date.parse(reservation.last_attempt)||0);await save(state);
        if(reservation.allowed){state.etag=reservation.etag;await fetchFeed(state,settings,reservation.lease);}
      }
    } else if(Date.now()-state.lastAttempt>=INTERVAL) {
      state.lastAttempt=Date.now();await save(state);await fetchFeed(state,settings);
    }
    if(state.paired)await mirrorPending(state);
    if(state.active){await completeActive(state);if(state.active){await save(state);return;}}
    if(Date.now()<(state.nextActionAt||0)){await save(state);return;}
    if(state.blocked){await save(state);return;}
    if(state.paired) {
      // Reconcile files obtained before the app was started; no re-download.
      await transferPending(state);
      await reconcileCompleted(state);
      const jobs=await native('jobs',{high_priority_only:true});
      if(jobs.ok&&jobs.auto_download&&jobs.items.find(highPriority))await processProduct(state,jobs.items.find(highPriority),true);
      else state.status=jobs.auto_download?'Windows連携で新着を待機しています':'Windows側で自動取得が停止中です';
    } else if(settings.auto) {
      const product=state.queue.find(p=>matches(p,settings));
      if(product)await processProduct(state,product,false);
      else state.status='拡張単体で新着を待機しています';
    }
    await chrome.action.setBadgeText({text:state.active?'↓':''});
  } catch(error) {
    state.connectionBlocked=error instanceof BridgeDisconnected;
    state.blocked=String(error.message||error);state.status=state.blocked;await notify(state.blocked);
  }
  await save(state);
}
async function initialize(){
  await chrome.alarms.create('collector-worker',{periodInMinutes:1});
  const config=await chrome.storage.local.get('settings');
  if(!config.settings){
    const status=await native('status');
    if(status.desktop&&status.configured)await chrome.storage.local.set({settings:{...defaults(),enabled:true,auto:!!status.settings?.auto_download}});
  }
}
chrome.runtime.onInstalled.addListener(()=>{void serial(async()=>{await initialize();await tick();});});
chrome.runtime.onStartup.addListener(()=>{void serial(async()=>{await initialize();await tick();});});
chrome.alarms.onAlarm.addListener(alarm=>{if(alarm.name==='collector-worker')void serial(()=>tick());});
chrome.downloads.onChanged.addListener(delta=>{if(delta.state||delta.error)void serial(()=>tick());});
chrome.runtime.onMessage.addListener((message,sender,reply)=>{
  if(sender.id!==chrome.runtime.id||message.target==='parser')return;
  if(!['view','save','resume','history'].includes(message.action))return;
  serial(async()=>{
    if(message.action==='save'){
      const settings={...defaults(),...message.settings};
      settings.search=String(settings.search||'').slice(0,200);settings.category=String(settings.category||'').slice(0,100);
      await chrome.storage.local.set({settings});await initialize();await tick(true);
    }
    if(message.action==='resume')await tick(true);
    if(message.action==='history'){
      const {state,settings,catalog}=await read();if(!catalog)throw Error('一覧をまだ取得していません');
      const result=await native('history',{high_priority_only:true});
      if(!result.desktop){for(const p of catalog.items)if(matches(p,settings)&&!state.queue.some(q=>q.id===p.id))state.queue.push(p);}
      else if(!result.ok)throw Error(result.error||'Windows処理待ち');
      await save(state);await tick(true);
    }
    const data=await read();return {ok:true,state:data.state,settings:data.settings};
  }).then(reply,error=>reply({ok:false,error:error.message}));return true;
});
void initialize();
