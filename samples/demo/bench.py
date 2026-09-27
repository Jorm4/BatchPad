"""Pretends to run four benchmarks and writes Google Benchmark JSON to out/bench.json."""
import argparse
import json
import os
import random
from datetime import datetime

parser = argparse.ArgumentParser(description=__doc__)
parser.parse_args()

base = [("BM_Parse", 1250.0, "ns", 50000), ("BM_Sort/1024", 2.2, "ms", 200),
        ("BM_Sort/65536", 180.0, "ms", 4), ("BM_Hash", 35.0, "us", 20000)]
benchmarks = []
for index, (name, time, unit, iterations) in enumerate(base):
    real = round(time * random.uniform(0.97, 1.03), 3)
    cpu = round(real * 0.98, 3)
    print(f"{name:<16}{real:>10} {unit}  {iterations} iterations")
    benchmarks.append({"name": name, "family_index": index, "per_family_instance_index": 0, "run_name": name,
                       "run_type": "iteration", "repetitions": 1, "repetition_index": 0, "threads": 1,
                       "iterations": iterations, "real_time": real, "cpu_time": cpu, "time_unit": unit})

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")
os.makedirs(out, exist_ok=True)
report = {"context": {"date": datetime.now().astimezone().isoformat(timespec="seconds"), "executable": "bench.py",
                      "library_build_type": "release"},
          "benchmarks": benchmarks}
with open(os.path.join(out, "bench.json"), "w", encoding="utf-8") as f:
    json.dump(report, f, indent=2)
