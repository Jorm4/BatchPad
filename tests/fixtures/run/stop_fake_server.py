"""Stop companion for fake_server.py: asks the server on --port to shut down."""
import argparse
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, required=True)
port = parser.parse_args().port
urllib.request.urlopen(f"http://127.0.0.1:{port}/shutdown", timeout=5).read()
print(f"Asked port {port} to stop.", flush=True)
