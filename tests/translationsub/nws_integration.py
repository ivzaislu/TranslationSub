#!/usr/bin/env python3
"""Exercise real Lampac NWS registrations, scoped fanout and reconnect.

Uses only Python stdlib: RFC6455 upgrade + masked client text frames.
"""
import base64
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
import socket
import sqlite3
import struct
import time
from urllib.request import Request, urlopen

HOST, PORT = "127.0.0.1", 9118
UID = "ts-nws-ci"
ROOT = "/tmp/lampac-translationsub-timecode/database"


class Ws:
    def __init__(self, name):
        self.s = socket.create_connection((HOST, PORT), timeout=10)
        self.buf = bytearray()
        key = base64.b64encode(os.urandom(16)).decode()
        request = (
            f"GET /nws?id={name}&ver=1 HTTP/1.1\r\n"
            f"Host: {HOST}:{PORT}\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            f"Sec-WebSocket-Key: {key}\r\n"
            "Sec-WebSocket-Version: 13\r\n\r\n"
        ).encode()
        self.s.sendall(request)
        response = bytearray()
        while b"\r\n\r\n" not in response:
            data = self.s.recv(4096)
            assert data, "WebSocket upgrade closed"
            response.extend(data)
        head, tail = bytes(response).split(b"\r\n\r\n", 1)
        assert head.startswith(b"HTTP/1.1 101"), head[:350]
        accept = base64.b64encode(hashlib.sha1(
            (key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()
        ).digest()).decode()
        assert b"Sec-WebSocket-Accept: " + accept.encode() in head or (
            b"sec-websocket-accept: " + accept.encode() in head.lower()
        ), head
        self.buf.extend(tail)
        welcome = self.read_message(timeout=5)
        assert welcome.get("method") == "Connected", welcome

    def send_text(self, value):
        payload = value.encode()
        size = len(payload)
        header = bytearray([0x81])
        if size < 126:
            header.append(0x80 | size)
        elif size < 65536:
            header.extend([0x80 | 126])
            header.extend(struct.pack("!H", size))
        else:
            header.extend([0x80 | 127])
            header.extend(struct.pack("!Q", size))
        mask = os.urandom(4)
        self.s.sendall(bytes(header) + mask +
                       bytes(byte ^ mask[i % 4] for i, byte in enumerate(payload)))

    def register(self, uid, profile):
        self.send_text(json.dumps({
            "method": "TranslationSubRegister", "args": [uid, profile]
        }))

    def read_exact(self, size):
        while len(self.buf) < size:
            data = self.s.recv(max(4096, size - len(self.buf)))
            if not data:
                raise ConnectionError("NWS connection closed")
            self.buf.extend(data)
        result = bytes(self.buf[:size])
        del self.buf[:size]
        return result

    def read_message(self, timeout=3):
        self.s.settimeout(timeout)
        while True:
            a, b = self.read_exact(2)
            length = b & 0x7f
            if length == 126:
                length = struct.unpack("!H", self.read_exact(2))[0]
            elif length == 127:
                length = struct.unpack("!Q", self.read_exact(8))[0]
            mask = self.read_exact(4) if (b & 0x80) else None
            body = self.read_exact(length)
            if mask:
                body = bytes(c ^ mask[i % 4] for i, c in enumerate(body))
            opcode = a & 0x0F
            if opcode == 8:
                raise ConnectionError("server closed NWS")
            if opcode == 1:
                if body == b"pong":
                    continue
                return json.loads(body)

    def close(self):
        self.s.close()


def post_timecode(episode, percent=90):
    url = f"http://{HOST}:{PORT}/timecode/set?uid={UID}&profile_id=7"
    payload = {
        "id": f"tv-123-s1e{episode}",
        "card": "123_tv",
        "percent": percent,
        "duration": 300,
        "position": 270,
        "watched_at": int(time.time() * 1000),
    }
    with urlopen(Request(
        url, method="POST",
        data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"},
    ), timeout=10) as response:
        body = json.load(response)
        assert body["success"] is True and body["accepted"] >= 1, body


with sqlite3.connect(ROOT + "/translationsub.db", timeout=10) as db:
    db.execute("DELETE FROM subscriptions WHERE uid = ?", (UID,))
    db.execute("DELETE FROM profile_progress WHERE uid = ?", (UID,))
    db.execute("""
        INSERT INTO subscriptions
          (id, uid, tmdb_id, content_id, title, original_title, is_serial,
           current_season, last_season, last_episode, sources_json, created_at,
           tmdb_new_season_available)
        VALUES (?, ?, '123', '123', 'NWS CI', 'NWS CI', 1, 1, 1, 5, '[]', ?, 0)
    """, ("ts-nws-sub", UID, str(time.time())))
    db.commit()

clients = []
try:
    for i in range(5):
        ws = Ws(f"ts-nws-target-{i}")
        ws.register(UID, "7")
        clients.append((ws, True))
    for i in range(3):
        ws = Ws(f"ts-nws-other-{i}")
        ws.register(UID if i < 2 else "ts-other-user", "8")
        clients.append((ws, False))
    time.sleep(0.3)

    post_timecode(3)

    def receive(pair):
        ws, targeted = pair
        try:
            msg = ws.read_message(timeout=5 if targeted else 1)
            return (targeted, msg)
        except socket.timeout:
            if targeted:
                raise AssertionError("Expected profile-scoped NWS notification")
            return (targeted, None)

    with ThreadPoolExecutor(max_workers=8) as pool:
        responses = list(pool.map(receive, clients))
    assert sum(1 for targeted, msg in responses if targeted and msg and
               msg.get("method") == "TranslationSubChanged") == 5, responses
    assert all(msg is None for targeted, msg in responses if not targeted), responses
    print("PASS: five concurrent NWS clients receive profile-scoped events; three isolated")

    for ws, _ in clients[:2]:
        ws.close()
    clients = clients[2:]
    reconnected = Ws("ts-nws-reconnect-0")
    reconnected.register(UID, "7")
    clients.append((reconnected, True))
    time.sleep(0.3)
    post_timecode(4)
    assert reconnected.read_message(timeout=5).get("method") == "TranslationSubChanged"
    print("PASS: disconnected client can reconnect and re-register")

    with urlopen(
        f"http://{HOST}:{PORT}/translationsub/v2/snapshot?uid={UID}&profile_id=7",
        timeout=10,
    ) as response:
        snapshot = json.load(response)
    row = next(x for x in snapshot["subscriptions"] if x["id"] == "ts-nws-sub")
    assert row["watchedEpisode"] == 4, row
    print("PASS: NWS mutation corresponds to persisted profile-specific SQLite projection")
finally:
    for ws, _ in clients:
        ws.close()

print("All NWS fanout/isolation/reconnect scenarios PASSED")
