const ids=['enabled','auto','linkWindows','includeAdult','includeExisting','search'];
const call=action=>chrome.runtime.sendMessage(action);
function render(data,settings=false){
  if(!data.ok){document.querySelector('#message').textContent=data.error;return;}
  const s=data.state;
  document.querySelector('#mode').textContent=s.paired?'Windows連携':'拡張単体';
  document.querySelector('#status').textContent=s.status;
  document.querySelector('#next').textContent=s.lastAttempt?`次回の一覧確認: ${new Date(s.lastAttempt+12*60*60*1000).toLocaleString('ja-JP')}`:'設定を保存すると開始します';
  document.querySelector('#counts').textContent=`取得履歴 ${Object.keys(s.done).length}ファイル ／ 拡張側の待機 ${s.queue.length}商品${s.importWarnings?` ／ 保存先への整理を保留 ${s.importWarnings}ファイル（元ファイルの移動・削除など。新着取得は継続）`:''}`;
  if(settings)for(const id of ids){const element=document.getElementById(id);if(element.type==='checkbox')element.checked=!!data.settings[id];else element.value=data.settings[id]||'';}
  document.getElementById('includeExisting').disabled=s.baseline;
}
async function act(action){
  const buttons=[...document.querySelectorAll('button')];buttons.forEach(b=>b.disabled=true);
  try{const data=await call(action);if(!data.ok)throw Error(data.error);render(data);document.querySelector('#message').textContent='';}catch(e){document.querySelector('#message').textContent=e.message;}
  finally{buttons.forEach(b=>b.disabled=false);}
}
document.getElementById('save').onclick=()=>{const settings={};for(const id of ids){const e=document.getElementById(id);settings[id]=e.type==='checkbox'?e.checked:e.value;}void act({action:'save',settings});};
document.getElementById('resume').onclick=()=>act({action:'resume'});
document.getElementById('history').onclick=()=>act({action:'history'});
document.getElementById('login').onclick=()=>chrome.tabs.create({url:'https://accounts.booth.pm/'});
render(await call({action:'view'}),true);
setInterval(async()=>render(await call({action:'view'})),3000);
