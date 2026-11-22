"""Exercise export framing limits without sending a large image or starting Vulkan."""
import struct
import subprocess
import sys
from pathlib import Path

exe = Path(sys.argv[1]).resolve()
for width, height, expected in [
    (8852, 5842, b'wrong byte length'),  # Rome full canvas, 51.7 MP
    (7722, 5151, b'wrong byte length'),  # Fuji native, >32 MP
    (16385, 1, b'Invalid integer'),
    (10001, 10000, b'up to 100,000,000 pixels'),
]:
    result = subprocess.run([str(exe), '--export-stream'], cwd=exe.parent,
                            input=f'FRAME {width} {height}\n'.encode(), capture_output=True, timeout=30)
    assert result.returncode != 0 and len(result.stdout) >= 8
    code, length = struct.unpack('<II', result.stdout[:8])
    message = result.stdout[8:]
    assert code == 1 and length == len(message) and expected in message, (width, height, message)
    print('PASS export bounds', width, height)

# Consume the complete valid frame before reporting an unsupported grain model.
# The renderer must preserve the choice and refuse it, never silently substitute.
with subprocess.Popen([str(exe), '--export-stream'], cwd=exe.parent,
                      stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE) as worker:
    worker.stdin.write(b'FRAME 8001 4000 --grain-model synthesis\n')
    remaining = 8001 * 4000 * 16
    chunk = bytes(8 * 1024 * 1024)
    while remaining:
        count = min(remaining, len(chunk))
        worker.stdin.write(chunk[:count])
        remaining -= count
    worker.stdin.close()
    output = worker.stdout.read()
    assert worker.wait(timeout=30) == 0
    code, length = struct.unpack('<II', output[:8])
    assert code == 1 and length == len(output[8:])
    assert b'Grain synthesis supports export canvases up to 32,000,000 pixels' in output[8:]
    assert b'grain quality was not reduced' in output[8:]
    print('PASS large synthesis grain is explicitly rejected without changing quality')
