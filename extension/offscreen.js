chrome.runtime.onMessage.addListener((message,sender,respond)=>{
  if(sender.id!==chrome.runtime.id||message.target!=='parser') return;
  try {
    const doc=new DOMParser().parseFromString(message.html,'text/html');
    const links=[...doc.querySelectorAll('a[href]')].map(a=>({url:a.getAttribute('href'),name:a.getAttribute('title')||''})).filter(a=>/^https:\/\/booth\.pm\/downloadables\/\d+\?/.test(a.url)&&a.name);
    respond({links});
  } catch { respond({error:'商品ページを解析できません'}); }
});
