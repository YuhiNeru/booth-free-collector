import importlib.util
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('export_feed', Path(__file__).resolve().parents[1] / 'deploy' / 'export_feed.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ExportTest(unittest.TestCase):
    def test_read_only_filter_and_public_fields(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parent) as directory:
            path = Path(directory)/'radar.sqlite'
            with closing(sqlite3.connect(path)) as db, db:
                db.executescript("CREATE TABLE meta(key,value); INSERT INTO meta VALUES('schema_version','catalog-v1'); CREATE TABLE runs(id,finished_at,status); INSERT INTO runs VALUES(1,'2026-10-25T09:00:00+00:00','ok'); CREATE TABLE products(id,title,shop,categories,tags,variants,age_rating,free_kind,first_seen,fetched_at,availability,price_min);")
                for ident, available, price, variants in [('1','public',0,[{'id':'10','price':0,'status':'free_download'},{'id':'11','price':500,'status':'on_sale'}]), ('2','public',500,[]), ('3','unavailable',0,[])]:
                    db.execute('INSERT INTO products VALUES(?,?,?,?,?,?,?,?,?,?,?,?)', (ident,'試験','shop','["3D"]','[]',json.dumps(variants),'all_ages','mixed_free_paid','2026-10-25','2026-10-25',available,price))
            before = path.read_bytes()
            result = json.loads(module.export(path))
            self.assertEqual(path.read_bytes(), before)
            self.assertEqual(result['interval_hours'], 12)
            self.assertEqual(len(result['items']), 1)
            self.assertEqual(result['items'][0]['variants'], [{'id':'10','name':''}])
            self.assertNotIn('source_url', result['items'][0])
            self.assertNotIn('description', result['items'][0])


if __name__ == '__main__':
    unittest.main()
