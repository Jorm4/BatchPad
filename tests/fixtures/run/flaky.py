"""Fails until flaky.marker beside it has been written twice, then succeeds."""
import os
import sys

marker = os.path.join(os.path.dirname(os.path.abspath(__file__)), "flaky.marker")
count = int(open(marker).read()) if os.path.exists(marker) else 0
if count < 2:
    with open(marker, "w") as f:
        f.write(str(count + 1))
    print(f"attempt {count + 1} failed", file=sys.stderr)
    sys.exit(1)
print("ok")
