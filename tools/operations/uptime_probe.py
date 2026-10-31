"""Independent, bounded uptime checks with a durable simulation-only outbox.

Run on another host/scheduler for actual independence. No email/network provider
is implemented: delivery outcomes are explicitly simulated or not_configured.
"""
import argparse
import json
import os
import sqlite3
import time
from contextlib import closing
from pathlib import Path
from urllib.error import URLError, HTTPError
from urllib.parse import urlsplit
from urllib.request import Request, HTTPRedirectHandler, build_opener


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def check(base, suffix, expected, timeout=5):
    target = base.rstrip("/") + suffix
    try:
        with build_opener(NoRedirect).open(Request(target, headers={"Accept": "application/json"}), timeout=timeout) as response:
            if response.geturl() != target or response.status != 200:
                return False
            return json.loads(response.read(4096)).get("status") == expected
    except (URLError, HTTPError, TimeoutError, ValueError, OSError):
        return False


def store(path, states, recipients=0, now=None):
    now = time.time() if now is None else now
    path.parent.mkdir(parents=True, exist_ok=True)
    with closing(sqlite3.connect(path, timeout=5)) as db, db:
        db.execute("CREATE TABLE IF NOT EXISTS incident (name TEXT PRIMARY KEY, healthy INTEGER NOT NULL, generation INTEGER NOT NULL, observed REAL NOT NULL)")
        db.execute("CREATE TABLE IF NOT EXISTS delivery (name TEXT NOT NULL, generation INTEGER NOT NULL, kind TEXT NOT NULL, created REAL NOT NULL, status TEXT NOT NULL, recipients INTEGER NOT NULL, action TEXT NOT NULL, PRIMARY KEY(name,generation,kind))")
        db.execute("BEGIN IMMEDIATE")
        for name, healthy in states.items():
            previous = db.execute("SELECT healthy,generation FROM incident WHERE name=?", (name,)).fetchone()
            generation = previous[1] if previous else 1
            if previous and previous[0] and not healthy:
                generation += 1
            changed = previous is not None and bool(previous[0]) != healthy or previous is None and not healthy
            if changed:
                kind = "recovery" if healthy else "P0_open"
                last = db.execute("SELECT max(created) FROM delivery WHERE name=? AND kind='P0_open' AND status!='suppressed'", (name,)).fetchone()[0]
                initial = db.execute("SELECT status FROM delivery WHERE name=? AND generation=? AND kind='P0_open'", (name, generation)).fetchone()
                suppressed = not healthy and last is not None and now - last < 1800 or healthy and initial and initial[0] == "suppressed"
                status = "suppressed" if suppressed else "simulated" if recipients else "not_configured"
                db.execute("INSERT OR IGNORE INTO delivery VALUES (?,?,?,?,?,?,?)", (name, generation, kind, now, status, recipients,
                    "Check independent network, application host and database; follow docs/OPERATIONS.md. No email was sent."))
            db.execute("INSERT INTO incident VALUES (?,?,?,?) ON CONFLICT(name) DO UPDATE SET healthy=excluded.healthy,generation=excluded.generation,observed=excluded.observed", (name, int(healthy), generation, now))
        return [dict(zip(("incident", "generation", "kind", "status"), row)) for row in db.execute("SELECT name,generation,kind,status FROM delivery ORDER BY created,name,generation,kind")]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--state-dir", type=Path, default=Path(__file__).resolve().parents[2] / ".local-operations")
    parser.add_argument("--allow-loopback-http", action="store_true")
    parser.add_argument("--simulate", choices=["down", "healthy"])
    args = parser.parse_args()
    parsed = urlsplit(args.base_url)
    loopback = parsed.hostname in ("localhost", "127.0.0.1", "::1")
    if parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path not in ("", "/") or not parsed.hostname or not (
        parsed.scheme == "https" or parsed.scheme == "http" and loopback and args.allow_loopback_http):
        parser.error("Use a configured HTTPS origin, or explicitly allow loopback HTTP for local tests.")
    if args.simulate:
        live = ready = args.simulate == "healthy"
    else:
        live = check(args.base_url, "/health/live", "up")
        # A failed application check cannot establish database health independently.
        ready = check(args.base_url, "/health/ready", "ready") if live else None
    states = {"application_unavailable": live}
    if ready is not None:
        states["database_unavailable"] = ready
    recipients = len([v for v in os.getenv("MYSHOPPINGLIST_INCIDENT_RECIPIENTS", "").split(",") if v.strip()])
    result = store(args.state_dir.resolve() / "uptime-simulation.sqlite3", states, recipients)
    print(json.dumps({"healthy": live and ready is True, "simulationOnly": True, "deliveries": result}))
    return 0 if live and ready is True else 1


if __name__ == "__main__":
    raise SystemExit(main())
