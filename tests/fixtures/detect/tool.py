"""Processes the named inputs."""
import argparse
import sys

print("tool.py must never run during detection", file=sys.stderr)
sys.exit(3)

parser = argparse.ArgumentParser()
parser.add_argument("--jobs", "-j", type=int, default=4, help="parallel jobs")
parser.add_argument("--gated", action="store_true", help="only gated tests")
parser.add_argument("--mode", choices=["fast", "full"], default="fast")
parser.add_argument("inputs", nargs="*", help="files to process")
args = parser.parse_args()
