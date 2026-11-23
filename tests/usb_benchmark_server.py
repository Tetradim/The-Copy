"""Small controlled USB benchmark endpoint. Default binding is Windows loopback."""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

MAX_BYTES = 32 * 1024 * 1024
CHUNK = bytes(64 * 1024)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def setup(self):
        super().setup()
        self.connection.settimeout(20)

    def log_message(self, _format, *args):
        pass

    def respond(self, status, body=b""):
        self.send_response(status)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if body:
            self.wfile.write(body)

    def do_GET(self):
        target = urlsplit(self.path)
        if target.path == "/ping":
            self.respond(200, b"OK")
            return
        if target.path != "/download":
            self.respond(404)
            return
        try:
            size = int(parse_qs(target.query)["bytes"][0])
            if not 1 <= size <= MAX_BYTES:
                raise ValueError
        except (KeyError, ValueError):
            self.respond(400)
            return
        self.send_response(200)
        self.send_header("Content-Length", str(size))
        self.end_headers()
        while size:
            count = min(size, len(CHUNK))
            self.wfile.write(CHUNK[:count])
            size -= count

    def do_POST(self):
        if self.path != "/upload":
            self.close_connection = True
            self.respond(404)
            return
        try:
            length = int(self.headers.get("Content-Length", ""))
            if not 1 <= length <= MAX_BYTES:
                raise ValueError
        except ValueError:
            self.close_connection = True
            self.respond(400)
            return
        left = length
        while left:
            block = self.rfile.read(min(left, len(CHUNK)))
            if not block:
                self.close_connection = True
                return
            left -= len(block)
        self.respond(200, str(length).encode("ascii"))


class BenchmarkServer(ThreadingHTTPServer):
    def handle_error(self, request, client_address):
        # The load sampler may close its socket after a phase finishes.
        import sys
        if isinstance(sys.exc_info()[1], (BrokenPipeError, ConnectionResetError)):
            return
        super().handle_error(request, client_address)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bind", default="127.0.0.1")
    parser.add_argument("--port", default=0, type=int)
    args = parser.parse_args()
    with BenchmarkServer((args.bind, args.port), Handler) as server:
        print(server.server_address[1], flush=True)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
