#!/usr/bin/env python3
"""Regression: terminal TMDB series are purged without touching TimeCode."""
from datetime import datetime, timezone
from urllib.request import Request, urlopen
import json
import sqlite3

ROOT = "/tmp/lampac-translationsub-timecode/database/"
LAMPAC = "http://127.0.0.1:9118"
MOCK = "http://127.0.0.1:9130"
UID = "ts-terminal-cleanup-ci"
OTHER_UID = "ts-terminal-other-ci"
DB = ROOT + "translationsub.db"
TIMECODE = ROOT + "TimeCode.sql"


def request(url, data=None):
    payload = None if data is None else json.dumps(data).encode()
    req = Request(url, data=payload, method="GET" if payload is None else "POST",
                  headers={} if payload is None else {"Content-Type": "application/json"})
    with urlopen(req, timeout=30) as response:
        return json.load(response)


def add(db, sub_id, uid, tmdb_id, status, season=1, voice=None):
    db.execute("""
        INSERT INTO subscriptions (
            id, uid, tmdb_id, content_id, title, translation_name, is_serial,
            current_season, last_season, last_episode, sources_json,
            created_at, tmdb_status, tmdb_new_season_available
        ) VALUES (?, ?, ?, ?, ?, ?, 1, ?, ?, 3, '[]', ?, ?, 0)
    """, (sub_id, uid, tmdb_id, tmdb_id or "unrelated-active", "Terminal CI",
          voice or sub_id, season, season, datetime.now(timezone.utc).isoformat(), status))
    db.execute("""
        INSERT INTO profile_progress
            (uid, profile_id, subscription_id, watched_episode, updated_at)
        VALUES (?, '7', ?, 2, ?)
    """, (uid, sub_id, datetime.now(timezone.utc).isoformat()))


rows = [
    # Stored statuses: these must be removed WITHOUT querying TMDB.
    ("stored-ended", UID, "930", "Ended", 1),
    ("stored-sibling", UID, "930", "Returning Series", 2),
    ("stored-canceled", UID, "931", "Canceled", 1),
    ("stored-cancelled", UID, "932", "Cancelled", 1),
    # Fresh statuses: every terminal spelling must be respected.
    ("fresh-ended", UID, "124", None, 1),
    ("fresh-sibling", UID, "124", "Returning Series", 2),
    ("fresh-canceled", UID, "125", None, 1),
    ("fresh-cancelled", UID, "126", None, 1),
    # Unrelated content and the same series under another user survive.
    ("unrelated-active", UID, None, "Returning Series", 1),
    ("other-user", OTHER_UID, "930", "Returning Series", 1),
]

with sqlite3.connect(DB) as db:
    db.execute("DELETE FROM profile_progress WHERE uid IN (?, ?)", (UID, OTHER_UID))
    db.execute("DELETE FROM subscriptions WHERE uid IN (?, ?)", (UID, OTHER_UID))
    for sub_id, uid, tmdb_id, status, season in rows:
        add(db, sub_id, uid, tmdb_id, status, season)
    db.commit()

with sqlite3.connect(TIMECODE) as db:
    timecode_rows_before = db.execute("SELECT COUNT(*) FROM timecodes").fetchone()[0]

stats_before = request(MOCK + "/__stats")["counts"]
balancer_paths = ("/balancer/one", "/balancer/two")
balancers_before = {path: stats_before.get(path, 0) for path in balancer_paths}

checked = request(LAMPAC + "/translationsub/v2/check?uid=" + UID, {})
assert checked.get("success") is True, checked
snapshot = checked["snapshot"]
assert [sub["id"] for sub in snapshot["subscriptions"]] == ["unrelated-active"], snapshot

with sqlite3.connect(DB) as db:
    remaining = {x[0] for x in db.execute(
        "SELECT id FROM subscriptions WHERE uid IN (?, ?)", (UID, OTHER_UID))}
    assert remaining == {"unrelated-active", "other-user"}, remaining
    remaining_progress = {x[0] for x in db.execute(
        "SELECT subscription_id FROM profile_progress WHERE uid IN (?, ?)",
        (UID, OTHER_UID))}
    assert remaining_progress == {"unrelated-active", "other-user"}, remaining_progress

with sqlite3.connect(TIMECODE) as db:
    timecode_rows_after = db.execute("SELECT COUNT(*) FROM timecodes").fetchone()[0]
assert timecode_rows_before == timecode_rows_after, (timecode_rows_before, timecode_rows_after)

stats_after = request(MOCK + "/__stats")["counts"]
for name in ("/tmdb/tv/930", "/tmdb/tv/931", "/tmdb/tv/932"):
    assert stats_after.get(name, 0) == stats_before.get(name, 0), name
for name in ("/tmdb/tv/124", "/tmdb/tv/125", "/tmdb/tv/126"):
    assert stats_after.get(name, 0) > stats_before.get(name, 0), name
for name in balancer_paths:
    assert stats_after.get(name, 0) == balancers_before[name], name
print("PASS: terminal statuses remove every voice/season, spare other user and TimeCode")

# Terminal detection must work when the user disables TMDB smart scheduling.
off_uid = "ts-terminal-tmdb-off-ci"
request(LAMPAC + "/translationsub/v2/settings?uid=" + off_uid,
        {"uid": off_uid, "sources": [], "useTmdbSchedule": False})
with sqlite3.connect(DB) as db:
    db.execute("DELETE FROM profile_progress WHERE uid = ?", (off_uid,))
    db.execute("DELETE FROM subscriptions WHERE uid = ?", (off_uid,))
    add(db, "smart-off-ended", off_uid, "127", None)
    db.commit()
before_off = request(MOCK + "/__stats")["counts"].get("/tmdb/tv/127", 0)
checked_off = request(LAMPAC + "/translationsub/v2/check?uid=" + off_uid, {})
assert checked_off.get("success") is True, checked_off
assert checked_off["snapshot"]["subscriptions"] == [], checked_off
with sqlite3.connect(DB) as db:
    assert db.execute("SELECT COUNT(*) FROM subscriptions WHERE uid = ?", (off_uid,)).fetchone()[0] == 0
    assert db.execute("SELECT COUNT(*) FROM profile_progress WHERE uid = ?", (off_uid,)).fetchone()[0] == 0
assert request(MOCK + "/__stats")["counts"].get("/tmdb/tv/127", 0) > before_off
print("PASS: TMDB status cleanup works with smart scheduling disabled")

# A new subscription for an already-ended series must never be recreated.
card = {
    "id": 124, "tmdb_id": 124, "imdb_id": "tt0000124",
    "name": "Terminal CI", "first_air_date": "2026-01-01",
    "number_of_seasons": 1, "source": "tmdb", "type": "tv",
}
rejected = request(LAMPAC + "/translationsub/v2/subscriptions?uid=" + UID,
                   {"uid": UID, "card": card, "voiceName": "CI Voice"})
assert rejected.get("success") is False and rejected.get("error") == "series_ended", rejected
with sqlite3.connect(DB) as db:
    assert db.execute("SELECT COUNT(*) FROM subscriptions WHERE uid = ? AND tmdb_id = '124'",
                      (UID,)).fetchone()[0] == 0
print("PASS: terminal TMDB status rejects a new subscription")
