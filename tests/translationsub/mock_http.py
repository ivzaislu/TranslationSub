#!/usr/bin/env python3
"""Deterministic local HTTP fixtures for TranslationSub CI (no live TMDB/balancers)."""
from collections import Counter
from datetime import date, timedelta
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import threading
from urllib.parse import urlsplit

PORT = 9130
counts = Counter()
lock = threading.Lock()
state = {"second_failed": False, "tmdb_failed": False}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass

    def respond(self, code, obj):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        path = urlsplit(self.path).path
        with lock:
            counts[path] += 1
            failed_second = state["second_failed"]
            failed_tmdb = state["tmdb_failed"]
        if path == "/__stats":
            with lock:
                return self.respond(200, {"counts": dict(counts), "state": dict(state)})
        if path == "/tmdb/tv/123":
            if failed_tmdb:
                return self.respond(503, {"status_message": "temporary unavailable"})
            return self.respond(200, {
                "id": 123, "name": "TranslationSub Smoke", "status": "Returning Series",
                "external_ids": {"imdb_id": "tt0000123"},
                "last_episode_to_air": {
                    "season_number": 1, "episode_number": 3, "air_date": "2026-01-01"
                },
                "next_episode_to_air": {
                    "season_number": 1, "episode_number": 4,
                    "air_date": str(date.today() + timedelta(days=7))
                },
                "seasons": [{"season_number": 1, "episode_count": 6}],
            })
        if path == "/tmdb/find/tt0000123":
            return self.respond(200, {"tv_results": [{"id": 123}]})
        if path == "/balancer/one":
            return self.respond(200, {
                "type": "episode",
                "data": [
                    {"s": 1, "e": n, "voice_name": "CI Voice", "voice_id": "ci-voice"}
                    for n in (1, 2, 3)
                ],
            })
        if path == "/balancer/two":
            if failed_second:
                return self.respond(503, {"error": "simulated provider outage"})
            return self.respond(200, {
                "type": "episode",
                "data": [
                    {"s": 1, "e": n, "voice_name": "CI Voice", "voice_id": "ci-voice"}
                    for n in (1, 2)
                ],
            })
        return self.respond(404, {"error": "not found", "path": path})

    def do_POST(self):
        if urlsplit(self.path).path != "/__mode":
            return self.respond(404, {"error": "not found"})
        try:
            data = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))))
            assert all(k in ("second_failed", "tmdb_failed") for k in data)
            assert all(isinstance(v, bool) for v in data.values())
        except (ValueError, AssertionError):
            return self.respond(400, {"error": "bad state"})
        with lock:
            state.update(data)
        return self.respond(200, {"ok": True, "state": state})


if __name__ == "__main__":
    server = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    print(f"Mock TMDB and Online balancers on 127.0.0.1:{PORT}", flush=True)
    server.serve_forever(poll_interval=0.2)
