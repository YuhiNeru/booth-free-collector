// Backpressure keeps one bounded chunk in flight; no full-file buffering.
export function transferPort(chrome, host) {
  const port=chrome.runtime.connectNative(host);
  let pending=null,closed=false;
  port.onMessage.addListener(message=>{if(!pending)return;const current=pending;pending=null;clearTimeout(current.timer);current.resolve(message);});
  port.onDisconnect.addListener(()=>{closed=true;if(pending){clearTimeout(pending.timer);pending.reject(Error('Windowsとの直接保存接続が切れました'));pending=null;}});
  return {
    request(command,body){
      if(closed||pending)return Promise.reject(Error('直接保存の接続が利用できません'));
      return new Promise((resolve,reject)=>{
        const timer=setTimeout(()=>{pending=null;reject(Error('直接保存の応答がありません'));port.disconnect();},30000);
        pending={resolve,reject,timer};port.postMessage({command,...body});
      }).then(result=>{if(!result.desktop||!result.ok)throw Error(result.error||'Windowsへの直接保存に失敗しました');return result;});
    },
    close(){port.disconnect();}
  };
}
export async function streamToWindows(response, ticket, transport) {
  const type=response.headers.get('Content-Type')||'';
  if(!response.ok||/text\/html|application\/xhtml/i.test(type))throw Error('ファイルではなくログイン・確認ページが返されました');
  const url=new URL(response.url);
  if(url.protocol!=='https:'||!(url.hostname==='booth.pm'||url.hostname.endsWith('.booth.pm')||url.hostname==='booth.pximg.net'))throw Error('許可されていないファイル配信先です');
  const raw=response.headers.get('Content-Length'),expected=raw&&/^\d+$/.test(raw)?Number(raw):null;
  // HTTP content encoding changes the byte count of the decoded stream.
  const size=response.headers.get('Content-Encoding')?null:expected;
  if(size!==null&&(size<=0||size>2147483648))throw Error('1ファイル2GiBの容量上限です');
  const reader=response.body?.getReader();if(!reader)throw Error('ファイル本文がありません');
  let total=0;
  try {
    await transport.request('begin_file',{ticket,size});
    while(true){
      const {done,value}=await reader.read();if(done)break;
      for(let pos=0;pos<value.length;pos+=192*1024){
        const bytes=value.subarray(pos,pos+192*1024);
        if(total+bytes.length>2147483648)throw Error('1ファイル2GiBの容量上限です');
        let binary='';for(let i=0;i<bytes.length;i+=8192)binary+=String.fromCharCode(...bytes.subarray(i,i+8192));
        const result=await transport.request('file_chunk',{ticket,offset:total,data:btoa(binary)});
        total+=bytes.length;if(result.received!==total)throw Error('直接保存の転送位置が一致しません');
      }
    }
    if(total===0||(size!==null&&total!==size))throw Error('ファイル容量が一致しません');
    return await transport.request('finish_file',{ticket,size:total});
  } catch(error){try{await transport.request('abort_file',{ticket});}catch{}throw error;}
  finally{await reader.cancel().catch(()=>{});}
}
