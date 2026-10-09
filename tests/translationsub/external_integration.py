#!/usr/bin/env python3
"""End-to-end API, TMDB and balancer regression checks against running Lampac."""
from urllib.request import Request, urlopen
from urllib.error import HTTPError
import json
import sqlite3

LAMPAC = "http://127.0.0.1:9118"
MOCK = "http://127.0.0.1:9130"
UID = "ts-external-ci"
CARD = {
    "id": 123,
    "tmdb_id": 123,
    "imdb_id": "tt0000123",
    "kinopoisk_id": 123,
    "name": "TranslationSub Smoke",
    "original_name": "TranslationSub Smoke",
    "first_air_date": "2026-01-01",
    "number_of_seasons": 1,
    "source": "tmdb",
    "type": "tv",
}


def json_request(url, data=None):
    raw = None if data is None else json.dumps(data).encode("utf-8")
    request = Request(
        url,
        data=raw,
        headers={"Content-Type": "application/json"} if raw else {},
        method="POST" if raw is not None else "GET",
    )
    try:
        with urlopen(request, timeout=20) as response:
            return json.load(response)
    except HTTPError as exc:
        raise AssertionError(f"{url} -> HTTP {exc.code}: {exc.read()[:500]}") from exc


def eq(value, expected, context):
    assert value == expected, f"{context}: expected {expected!r}, got {value!r}"


def sources(state):
    return {row["source"] for voice in state.get("voices", []) for row in voice.get("sources", [])}


base = f"{LAMPAC}/translationsub/v2"
settings = json_request(f"{base}/settings?uid={UID}")
available = {s["id"].lower() for s in settings["availableSourceItems"]}
assert {"collaps", "hdvb"}.issubset(available), available
json_request(
    f"{base}/settings?uid={UID}",
    {"uid": UID, "sources": ["collaps", "hdvb"], "checkIntervalHours": 1,
     "useTmdbSchedule": True},
)
print("PASS: registered local and override Online sources")

payload = {"uid": UID, "card": CARD, "includeVoices": True}
state = json_request(f"{base}/content-state?uid={UID}", payload)
print("Mock request counters after content-state:",
      json_request(f"{MOCK}/__stats")["counts"], flush=True)
assert state.get("eligible") is True, state
assert len(state.get("voices", [])) == 1, state
eq(state["voices"][0]["latestEpisode"], 3, "highest available episode")
assert {"collaps", "hdvb"} == sources(state), state
assert state["content"]["tmdbId"] == "123", state
print("PASS: both mock balancers merge the same voice; mock TMDB resolves content")

stats = json_request(f"{MOCK}/__stats")["counts"]
before = {name: stats.get(name, 0) for name in ("/balancer/one", "/balancer/two")}
card_only = json_request(f"{base}/content-state?uid={UID}",
                         {"uid": UID, "card": CARD, "includeVoices": False})
assert card_only["eligible"] is True and not card_only["voices"], card_only
after = json_request(f"{MOCK}/__stats")["counts"]
assert all(before[name] == after.get(name, 0) for name in before), (before, after)
print("PASS: light card request does not fan out to online sources")

subscribe = json_request(f"{base}/subscriptions?uid={UID}",
                         {"uid": UID, "card": CARD, "voiceName": "CI Voice"})
assert subscribe.get("success") is True, subscribe
sub_id = subscribe["subscriptionId"]
again = json_request(f"{base}/subscriptions?uid={UID}",
                     {"uid": UID, "card": CARD, "voiceName": "CI Voice"})
assert again.get("success") is True and again["subscriptionId"] == sub_id, again
snapshot = json_request(f"{base}/snapshot?uid={UID}")
rows = [x for x in snapshot["subscriptions"] if x["id"] == sub_id]
assert len(rows) == 1 and rows[0]["availableEpisode"] == 3, snapshot
assert rows[0]["tmdb"]["lastEpisode"] == 3, rows[0]
with sqlite3.connect("/tmp/lampac-translationsub-timecode/database/translationsub.db") as db:
    count = db.execute("select count(*) from subscriptions where uid = ?", (UID,)).fetchone()[0]
    eq(count, 1, "SQLite subscription deduplication")
print("PASS: subscription, idempotent duplicate, persisted TMDB schedule and SQLite")

json_request(f"{MOCK}/__mode", {"second_failed": True})
degraded = json_request(f"{base}/content-state?uid={UID}", payload)
assert degraded["eligible"] is True and len(degraded.get("voices", [])) == 1, degraded
eq(sources(degraded), {"collaps"}, "partial outage source isolation")
json_request(f"{MOCK}/__mode", {"second_failed": False})
print("PASS: failed balancer does not hide a working balancer")

missing_tmdb = dict(CARD, id=999, tmdb_id=999, imdb_id="tt9999999")
graceful = json_request(f"{base}/content-state?uid={UID}",
                        {"uid": UID, "card": missing_tmdb, "includeVoices": False})
assert graceful["eligible"] is True and graceful["content"]["tmdbId"] == "999", graceful
print("PASS: TMDB unavailable fallback preserves card identity")

removed = json_request(f"{base}/subscriptions/{sub_id}/remove?uid={UID}", {})
assert removed.get("success") is True, removed
remaining = json_request(f"{base}/snapshot?uid={UID}")
assert all(x["id"] != sub_id for x in remaining["subscriptions"]), remaining
print("PASS: unsubscribe removes subscription from persisted snapshot")

stats = json_request(f"{MOCK}/__stats")["counts"]
for key in ("/tmdb/tv/123", "/balancer/one", "/balancer/two", "/tmdb/tv/999"):
    assert stats.get(key, 0) >= 1, (key, stats)
print("All mock TMDB / Online integration scenarios PASSED")
