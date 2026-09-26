"""Serves on --port until GET /shutdown, like a dev server that prints 'Press Ctrl+C to stop.'"""
import argparse
import http.server
import threading

parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, required=True)
port = parser.parse_args().port


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b"ok")
        if self.path == "/shutdown":
            threading.Thread(target=server.shutdown).start()

    def log_message(self, *args):
        pass


server = http.server.HTTPServer(("127.0.0.1", port), Handler)
print(f"Serving on http://127.0.0.1:{port}/", flush=True)
print("Press Ctrl+C to stop.", flush=True)
server.serve_forever()
print("Stopped.", flush=True)
