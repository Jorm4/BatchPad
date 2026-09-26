"""Prints its arguments as one JSON array."""
import json
import sys

print(json.dumps(sys.argv[1:]))
