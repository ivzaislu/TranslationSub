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
notice = rows[0]["display"].get("noticeSources") or ""
assert "collaps" in notice.lower() and "hdvb" not in notice.lower(), notice
assert "showNoticeSources" in settings["schema"], settings
assert settings["schema"]["showNoticeSources"]["defaultValue"] is True, settings
print("PASS: notifications identify only the balancer hosting the newest episode")
with sqlite3.connect("/tmp/lampac-translationsub-timecode/database/translationsub.db") as db:
    count = db.execute("select count(*) from subscriptions where uid = ?", (UID,)).fetchone()[0]
    eq(count, 1, "SQLite subscription deduplication")
print("PASS: subscription, idempotent duplicate, persisted TMDB schedule and SQLite")

# A subscription persisted by an older version has source IDs but no
# per-balancer episode counters. Do not guess which provider has E3.
with sqlite3.connect("/tmp/lampac-translationsub-timecode/database/translationsub.db") as db:
    (saved_sources,) = db.execute(
        "SELECT sources_json FROM subscriptions WHERE id = ?", (sub_id,)).fetchone()
    legacy_sources = [
        {k: v for k, v in entry.items() if k != "LatestEpisode"}
        for entry in json.loads(saved_sources)
    ]
    db.execute("UPDATE subscriptions SET sources_json = ? WHERE id = ?",
               (json.dumps(legacy_sources), sub_id))
    db.commit()
legacy_snapshot = json_request(f"{base}/snapshot?uid={UID}")
legacy_row = next(x for x in legacy_snapshot["subscriptions"] if x["id"] == sub_id)
assert not legacy_row["display"].get("noticeSources"), legacy_row
with sqlite3.connect("/tmp/lampac-translationsub-timecode/database/translationsub.db") as db:
    db.execute("UPDATE subscriptions SET sources_json = ? WHERE id = ?",
               (saved_sources, sub_id))
    db.commit()
print("PASS: legacy subscription sources without episode counts do not produce false labels")

# Server controls the notice line; hiding it does not hide ordinary source metadata.
disabled = json_request(f"{base}/settings?uid={UID}",
                        {"uid": UID, "showNoticeSources": False})
assert disabled["settings"]["ShowNoticeSources"] is False, disabled
snapshot_off = json_request(f"{base}/snapshot?uid={UID}")
row_off = next(x for x in snapshot_off["subscriptions"] if x["id"] == sub_id)
assert not row_off["display"].get("noticeSources"), row_off
assert len(row_off["sources"]) == 2, row_off
assert json_request(f"{base}/settings?uid={UID}")["settings"]["ShowNoticeSources"] is False
print("PASS: disabled notice-balancer setting persists without dropping source metadata")

enabled = json_request(f"{base}/settings?uid={UID}",
                       {"uid": UID, "showNoticeSources": True})
assert enabled["settings"]["ShowNoticeSources"] is True, enabled

# Both sources now report episode E3: both labels should be shown.
json_request(f"{MOCK}/__mode", {"second_latest": True})
multi = json_request(f"{base}/check?uid={UID}", {})
assert multi["success"] is True, multi
multi_row = next(x for x in multi["snapshot"]["subscriptions"] if x["id"] == sub_id)
multi_notice = multi_row["display"].get("noticeSources") or ""
assert multi_notice.lower().startswith("балансеры: "), multi_notice
assert "collaps" in multi_notice.lower() and "hdvb" in multi_notice.lower(), multi_notice
print("PASS: both balancers are shown if both carry the latest episode")

# When second source falls behind again, its name must disappear.
json_request(f"{MOCK}/__mode", {"second_latest": False})
single = json_request(f"{base}/check?uid={UID}", {})
assert single["success"] is True, single
single_row = next(x for x in single["snapshot"]["subscriptions"] if x["id"] == sub_id)
single_notice = single_row["display"].get("noticeSources") or ""
assert "collaps" in single_notice.lower() and "hdvb" not in single_notice.lower(), single_notice
print("PASS: outdated balancers are not credited with the newest episode")

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
