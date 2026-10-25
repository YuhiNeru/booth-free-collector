"""12-hour durable crawl gate. The desktop cannot invoke this process."""
import datetime as dt
import fcntl
import json
import os
from pathlib import Path
import subprocess
import sys

STATE = Path('/var/lib/booth-free-radar')


def main():
    with (STATE / 'collector-crawl.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        stamp = STATE / 'collector-crawl-at.json'
        now = dt.datetime.now(dt.timezone.utc)
        if stamp.exists():
            last = dt.datetime.fromisoformat(json.loads(stamp.read_text())['at'])
            if (now - last).total_seconds() < 12 * 3600:
                print('Crawl skipped: 12-hour interval has not elapsed')
                return
        temp = stamp.with_suffix('.tmp')
        temp.write_text(json.dumps({'at': now.isoformat()}))
        temp.replace(stamp)
        crawl = subprocess.run([sys.executable, '-m', 'radar.cli', '--config', '/etc/booth-free-radar/config.toml',
                                '--env-file', '/etc/booth-free-radar/secrets.env', 'run'], check=False)
        if crawl.returncode:
            raise SystemExit(crawl.returncode)
        publish = subprocess.run([sys.executable, '/opt/booth-free-collector/export_feed.py', '--endpoint',
                                  'https://booth-free-collector.vrc-ranking-board.workers.dev/publish'], check=False)
        if publish.returncode:
            print('Catalog was collected; snapshot publication failed. Last published feed retained.', file=sys.stderr)
            raise SystemExit(publish.returncode)


if __name__ == '__main__':
    main()
