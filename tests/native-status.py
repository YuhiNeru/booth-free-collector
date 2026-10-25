"""Read-only local runtime status. Does not access the browser profile."""
import json
import struct
import subprocess
import sys

exe = 'C:/Code/booth-free-collector/dist/BoothFreeCollector/BoothFreeCollector.exe'
body = json.dumps({'command': 'status'}).encode()
result = subprocess.run([exe, 'chrome-extension://ipocnbpbmnbjhliliadogibolpcmnjhd/'],
                        input=struct.pack('<I', len(body)) + body, capture_output=True, timeout=15)
if len(result.stdout) < 4:
    sys.exit('No native host reply')
size = struct.unpack('<I', result.stdout[:4])[0]
status = json.loads(result.stdout[4:4 + size])
summary = {k: v for k, v in status.items() if k != 'settings'}
summary['follow_library_folder'] = status.get('settings', {}).get('follow_library_folder')
summary['auto_download'] = status.get('settings', {}).get('auto_download')
print(json.dumps(summary, ensure_ascii=False, indent=2))
