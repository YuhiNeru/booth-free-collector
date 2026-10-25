export const FEED_URL = 'https://booth-free-collector.vrc-ranking-board.workers.dev/catalog.json';
export const INTERVAL = 12 * 60 * 60 * 1000;
export const initialState = () => ({lastAttempt:0,lastSuccess:0,baseline:false,seen:{},queue:[],done:{},active:null,blocked:'',status:'初期設定後に開始します',generatedAt:null,paired:false});
export const defaults = () => ({enabled:false,auto:true,linkWindows:true,includeAdult:false,includeExisting:true,search:'',category:''});
export const highPriority = p => (p.categories||[]).some(c=>['3D衣装','衣装','3D髪型','髪型'].includes(c));
export const safeId = value => typeof value === 'string' && /^\d{1,20}$/.test(value);
export function safeName(value) {
  if (typeof value !== 'string' || !value || value.length > 180 || /[<>:"/\\|?*\x00-\x1f]/.test(value) || /[ .]$/.test(value) || /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)/i.test(value)) throw Error('安全に保存できないファイル名です');
  return value;
}
export function validateFeed(feed) {
  if (feed?.schema !== 1 || feed.interval_hours !== 12 || !Array.isArray(feed.items) || feed.items.length > 30000 || !Number.isFinite(Date.parse(feed.generated_at))) throw Error('一覧の形式が一致しません');
  const ids = new Set();
  for (const p of feed.items) {
    if (!safeId(p.id) || ids.has(p.id) || p.url !== `https://booth.pm/ja/items/${p.id}` || typeof p.title !== 'string' || !Array.isArray(p.variants) || !p.variants.length || p.variants.some(v=>!safeId(v.id))) throw Error('商品データが不正です');
    ids.add(p.id);
  }
  return feed;
}
export function matches(p,s) {
  const text=[p.title,p.shop,...(p.tags||[])].join(' ').normalize('NFKC').toLowerCase();
  return highPriority(p) && (s.includeAdult || p.age_rating==='all_ages') && s.search.normalize('NFKC').toLowerCase().split(/\s+/).filter(Boolean).every(t=>text.includes(t));
}
export function applyFeed(state,feed,settings) {
  validateFeed(feed);
  if (state.generatedAt && Date.parse(feed.generated_at)<Date.parse(state.generatedAt)) throw Error('古い一覧は適用しません');
  const baseline=!state.baseline&&!settings.includeExisting;
  for(const p of feed.items) {
    const known=state.seen[p.id];
    if ((!known||p.variants.some(v=>!known.includes(v.id)))&&!baseline&&matches(p,settings)&&!state.queue.some(q=>q.id===p.id)) state.queue.push(p);
    state.seen[p.id]=p.variants.map(v=>v.id);
  }
  const current=new Map(feed.items.map(p=>[p.id,p]));
  state.queue=state.queue.filter(p=>current.has(p.id)).map(p=>current.get(p.id));
  state.baseline=true; state.generatedAt=feed.generated_at; state.lastSuccess=Date.now(); state.blocked='';
  state.status=baseline?`初回${feed.items.length.toLocaleString()}件を基準にしました`:`待機中 ${state.queue.length}商品`;
  return state;
}
export function validateLink(product,entry) {
  const match=typeof entry.url==='string'&&entry.url.match(/^https:\/\/booth\.pm\/downloadables\/(\d+)\?variation_id=(\d+)$/);
  if (!match||!product.variants.some(v=>v.id===match[2])) throw Error('無料バリエーションのリンクではありません');
  return {productId:product.id,fileId:match[1],variantId:match[2],name:safeName(entry.name),url:entry.url,key:`${product.id}/${match[1]}`};
}
