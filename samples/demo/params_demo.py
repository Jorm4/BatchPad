"""Prints the arguments each parameter type produces."""
import sys

print("params_demo got", len(sys.argv) - 1, "arguments:")
for arg in sys.argv[1:]:
    print(" ", arg)
