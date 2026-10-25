"""Publish only public product metadata. Opens the production catalog read-only."""
import argparse
from contextlib import closing
import datetime as dt
import json
from pathlib import Path
import sqlite3
import urllib.request

MAX_BYTES = 12 * 1024 * 1024


def export(db_path):
    with closing(sqlite3.connect(Path(db_path).resolve().as_uri() + '?mode=ro', uri=True, timeout=5)) as db:
        db.row_factory = sqlite3.Row
        if db.execute("SELECT value FROM meta WHERE key='schema_version'").fetchone()[0] != 'catalog-v1':
            raise ValueError('Unsupported catalog schema')
        run = db.execute("SELECT finished_at,status FROM runs WHERE finished_at IS NOT NULL ORDER BY id DESC LIMIT 1").fetchone()
        items = []
        for row in db.execute("SELECT id,title,shop,categories,tags,variants,age_rating,free_kind,first_seen,fetched_at FROM products WHERE availability='public' AND price_min=0 AND fetched_at IS NOT NULL ORDER BY first_seen,id"):
            p = dict(row)
            variants = [v for v in json.loads(p.pop('variants') or '[]') if v.get('price') == 0 and (v.get('status') == 'free_download' or v.get('download_available'))]
            if not variants:
                continue
            p['variants'] = [{'id': v['id'], 'name': v.get('name') or ''} for v in variants if str(v.get('id', '')).isdigit()]
            if not p['variants']:
                continue
            for key in ('categories', 'tags'):
                p[key] = json.loads(p[key] or '[]')
            p['url'] = 'https://booth.pm/ja/items/' + p['id']
            items.append(p)
        result = dict(schema=1, generated_at=dt.datetime.now(dt.timezone.utc).isoformat(),
                      crawler_finished_at=run['finished_at'] if run else None,
                      crawler_status=run['status'] if run else 'unknown', interval_hours=12, items=items)
        payload = json.dumps(result, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
        if len(payload) > MAX_BYTES:
            raise ValueError('Feed exceeds size limit; last published snapshot retained')
        return payload


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--db', default='/var/lib/booth-free-radar/radar.sqlite')
    parser.add_argument('--output')
    parser.add_argument('--endpoint')
    parser.add_argument('--secret-file', default='/etc/booth-free-collector/publish.secret')
    args = parser.parse_args()
    payload = export(args.db)
    if args.output:
        dest = Path(args.output)
        dest.parent.mkdir(parents=True, exist_ok=True)
        tmp = dest.with_suffix('.tmp')
        tmp.write_bytes(payload)
        tmp.replace(dest)
    if args.endpoint:
        if not args.endpoint.startswith('https://') or not args.endpoint.endswith('/publish'):
            raise ValueError('HTTPS publish endpoint required')
        token = Path(args.secret_file).read_text().strip()
        # No redirect handler: never forward the publishing credential elsewhere.
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *args, **kwargs):
                return None
        request = urllib.request.Request(args.endpoint, data=payload, method='PUT', headers={
            'Authorization': 'Bearer ' + token, 'Content-Type': 'application/json',
            'User-Agent': 'booth-free-collector-publisher/1'})
        with urllib.request.build_opener(NoRedirect).open(request, timeout=45) as response:
            if response.status != 200:
                raise RuntimeError('Publish failed')
        print('Published', len(json.loads(payload)['items']), 'items')


if __name__ == '__main__':
    main()
