"""Loopback HTTP and length-prefixed TCP peers for the Unity network demo."""
import argparse
import json
import socketserver
import struct
import threading
import time
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class HttpHandler(BaseHTTPRequestHandler):
    def respond(self, status, body):
        self.send_response(status)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("X-Fixture", "loopback")
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass  # The cancellation and timeout cases intentionally leave early.

    def do_GET(self):
        if self.path == "/hold":
            self.close_connection = True
            self.rfile.read(1)  # Real Play exit must abort the pending request.
            return
        if self.path == "/delay":
            time.sleep(3)
        status = {"/config": 200, "/delay": 200, "/error": 503}.get(self.path, 404)
        self.respond(status, b"network-demo" if status == 200 else b"fixture-error")

    def do_POST(self):
        if self.path != "/echo" or self.headers.get("X-Demo") != "binary":
            self.respond(400, b"invalid echo request")
            return
        self.respond(200, self.rfile.read(int(self.headers["Content-Length"])))

    def log_message(self, *args):
        pass


def frame(body):
    return struct.pack(">I", len(body)) + body


class MessageHandler(socketserver.StreamRequestHandler):
    def handle(self):
        try:
            self.wfile.write(frame(b"welcome"))
            while True:
                header = self.rfile.read(4)
                if len(header) != 4:
                    return
                length, = struct.unpack(">I", header)
                if length > 8 * 1024 * 1024:
                    return
                body = self.rfile.read(length)
                if len(body) != length or body == b"/drop":
                    return
                if body == b"/frames":
                    wire = frame(b"split") + frame(b"joined") + frame(b"")
                    for chunk in (wire[:2], wire[2:6], wire[6:]):
                        self.wfile.write(chunk)
                        time.sleep(.015)
                elif body == b"/oversize":
                    self.wfile.write(struct.pack(">I", 8 * 1024 * 1024 + 1))
                    return
                elif body == b"/truncate":
                    self.wfile.write(frame(b"partial")[:-1])
                    return
                elif body == b"/stall":
                    time.sleep(3)
                    return
                else:
                    self.wfile.write(frame(body))
        except (ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
            pass


@contextmanager
def serve(http_port=0, tcp_port=0):
    with ThreadingHTTPServer(("127.0.0.1", http_port), HttpHandler) as http, \
            socketserver.ThreadingTCPServer(("127.0.0.1", tcp_port), MessageHandler) as tcp:
        tcp.daemon_threads = True
        for server in (http, tcp):
            threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            yield http.server_port, tcp.server_address[1]
        finally:
            tcp.shutdown()
            http.shutdown()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--http", type=int, default=0)
    parser.add_argument("--tcp", type=int, default=0)
    args = parser.parse_args()
    with serve(args.http, args.tcp) as ports:
        print(json.dumps(dict(http=ports[0], tcp=ports[1])), flush=True)
        try:
            threading.Event().wait()
        except KeyboardInterrupt:
            pass
