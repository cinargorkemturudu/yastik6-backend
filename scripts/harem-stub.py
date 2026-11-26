#!/usr/bin/env python3
# Local stand-in for the Harem RapidAPI, so the backend can run without a RapidAPI key.
# Answers every GET with a small, valid Harem payload; the timestamp is "now" so it's never stale.
import json
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        now = datetime.now().strftime("%d.%m.%Y %H:%M:%S")
        items = [("ONS", "2.650,10", "2.651,40"), ("YENİ TAM", "28.950,00", "29.420,00"), ("ESKİ TAM", "28.700,00", "29.250,00")]
        body = json.dumps({"success": True, "data": [
            {"key": k, "code": k, "buy": b, "sell": s, "last_update": now} for k, b, s in items
        ]}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass

ThreadingHTTPServer(("127.0.0.1", 8765), Handler).serve_forever()
