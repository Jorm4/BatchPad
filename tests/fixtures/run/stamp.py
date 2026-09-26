"""Writes <name>.stamp beside itself: a "start <seconds>" line, then an "end <seconds>" line.

stamp.py <name> [seconds]        sleeps between start and end (default 0.4 s)
stamp.py <name> --meet <count>   waits until <count> stamps have started (10 s at most),
                                 so runs that are really concurrent always overlap

One file per name, because concurrent appends to a shared file can lose lines on Windows.
"""
import glob
import os
import sys
import time

args = sys.argv[1:]
meet = 0
if "--meet" in args:
    at = args.index("--meet")
    meet = int(args[at + 1])
    del args[at:at + 2]
name, seconds = args[0], float(args[1]) if len(args) > 1 else 0.4
here = os.path.dirname(os.path.abspath(__file__))
path = os.path.join(here, f"{name}.stamp")


def stamp(event):
    with open(path, "a", encoding="utf-8") as f:
        f.write(f"{event} {time.time()}\n")


stamp("start")
if meet:
    deadline = time.time() + 10
    while len(glob.glob(os.path.join(here, "*.stamp"))) < meet and time.time() < deadline:
        time.sleep(0.02)
else:
    time.sleep(seconds)
stamp("end")
