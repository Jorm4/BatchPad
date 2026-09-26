"""Fails its first --failures runs, then passes and starts counting again."""
import argparse
import os
import sys

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--failures", type=int, default=1, help="how many runs fail before one passes")
args = parser.parse_args()

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")
os.makedirs(out, exist_ok=True)
counter = os.path.join(out, "flaky.count")
count = int(open(counter).read()) if os.path.exists(counter) else 0
if count < args.failures:
    with open(counter, "w") as f:
        f.write(str(count + 1))
    print(f"attempt {count + 1}: simulated failure", file=sys.stderr)
    sys.exit(1)
if os.path.exists(counter):
    os.remove(counter)
print(f"passed after {count} failure(s)")
