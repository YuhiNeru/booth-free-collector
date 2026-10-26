"""Exercise the Linux durable gate without any crawler or network calls."""
import datetime as dt
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import patch


@unittest.skipUnless(os.name == 'posix', 'Linux flock gate')
class CrawlGateTest(unittest.TestCase):
    def setUp(self):
        source = Path(os.environ.get('BOOTH_GATE_TEST_SOURCE', Path(__file__).resolve().parents[1] / 'deploy/run_crawl.py'))
        spec = importlib.util.spec_from_file_location('collector_gate_fixture', source)
        self.gate = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.gate)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.gate.STATE = Path(self.temp.name)

    def test_failure_reserves_interval_and_boundary_allows_next_batch(self):
        calls = []
        def fail(command, **kwargs):
            calls.append(command)
            return types.SimpleNamespace(returncode=1)
        with patch.object(self.gate.subprocess, 'run', fail):
            with self.assertRaises(SystemExit):
                self.gate.main()
            self.gate.main()
        self.assertEqual(len(calls), 1)
        stamp = self.gate.STATE / 'collector-crawl-at.json'
        last = dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=12, seconds=1)
        stamp.write_text(json.dumps({'at': last.isoformat()}))
        def success(command, **kwargs):
            calls.append(command)
            return types.SimpleNamespace(returncode=0)
        with patch.object(self.gate.subprocess, 'run', success):
            self.gate.main()
            self.gate.main()
        self.assertEqual(len(calls), 3)  # one failed crawl, one crawl and one publication

    def test_competing_process_and_corrupt_stamp_never_start_crawl(self):
        with patch.object(self.gate.subprocess, 'run') as run:
            with (self.gate.STATE / 'collector-crawl.lock').open('a') as held:
                self.gate.fcntl.flock(held, self.gate.fcntl.LOCK_EX | self.gate.fcntl.LOCK_NB)
                with self.assertRaises(BlockingIOError):
                    self.gate.main()
            (self.gate.STATE / 'collector-crawl-at.json').write_text('{invalid')
            with self.assertRaises(json.JSONDecodeError):
                self.gate.main()
            run.assert_not_called()


if __name__ == '__main__':
    unittest.main()
