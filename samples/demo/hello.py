"""Prints a greeting and the arguments it was given."""
import sys

print("Hello from Python!", *sys.argv[1:])
