"""Runs three pretend tests and writes their results as JUnit XML to out/junit.xml."""
import argparse
import os
from xml.sax.saxutils import quoteattr

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--fail", action="store_true", help="make one test fail")
args = parser.parse_args()

tests = [("physics", "gravity", None), ("physics", "collision", "expected 2 contacts, got 3" if args.fail else None),
         ("audio", "mixer", None)]
cases = []
for suite, name, failure in tests:
    print(f"{suite}.{name} ... {'FAILED' if failure else 'ok'}")
    body = f"<failure message={quoteattr(failure)} />" if failure else ""
    cases.append(f'    <testcase classname="{suite}" name="{name}" time="0.01">{body}</testcase>')

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")
os.makedirs(out, exist_ok=True)
failures = sum(1 for t in tests if t[2])
with open(os.path.join(out, "junit.xml"), "w", encoding="utf-8") as f:
    f.write(f'<testsuite name="demo" tests="{len(tests)}" failures="{failures}">\n' + "\n".join(cases) + "\n</testsuite>\n")
print(f"{len(tests) - failures} passed, {failures} failed")
raise SystemExit(1 if failures else 0)
