"""Independent, bounded uptime checks with a durable outbox. Email requires explicit opt-in.

Run on another host/scheduler for actual independence. Default delivery remains simulated.
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
from smtp_notifications import settings_from_env, send_notification


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


def store(path, states, recipients=0, now=None, send_email=False):
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
                last = db.execute("SELECT max(created) FROM delivery WHERE name=? AND kind='P0_open' AND (status IN ('queued','sending','sent') OR (status='simulated' AND ?))", (name, int(not send_email))).fetchone()[0]
                initial = db.execute("SELECT status FROM delivery WHERE name=? AND generation=? AND kind='P0_open'", (name, generation)).fetchone()
                opened = initial and initial[0] in ("queued", "sending", "sent", "simulated")
                suppressed = not healthy and last is not None and now - last < 1800 or healthy and not opened
                status = "suppressed" if suppressed else "queued" if recipients and send_email else "simulated" if recipients else "not_configured"
                db.execute("INSERT OR IGNORE INTO delivery (name,generation,kind,created,status,recipients,action) VALUES (?,?,?,?,?,?,?)", (name, generation, kind, now, status, recipients,
                    "Follow docs/OPERATIONS.md. Notification queued." if status == "queued" else "Follow docs/OPERATIONS.md. No email was sent."))
            db.execute("INSERT INTO incident VALUES (?,?,?,?) ON CONFLICT(name) DO UPDATE SET healthy=excluded.healthy,generation=excluded.generation,observed=excluded.observed", (name, int(healthy), generation, now))
        db.execute("DELETE FROM delivery WHERE created<? AND status NOT IN ('queued','sending')", (now - 30 * 86400,))
        rows = db.execute("SELECT name,generation,kind,status FROM delivery ORDER BY created DESC,name DESC,generation DESC,kind DESC LIMIT 50").fetchall()
        return [dict(zip(("incident", "generation", "kind", "status"), row)) for row in reversed(rows)]


def deliver(path, settings, now=None, sender=send_notification):
    now = time.time() if now is None else now
    with closing(sqlite3.connect(path, timeout=5)) as db, db:
        db.execute("BEGIN IMMEDIATE")
        columns = {row[1] for row in db.execute("PRAGMA table_info(delivery)")}
        for name, declaration in (("attempts", "INTEGER NOT NULL DEFAULT 0"), ("next_attempt", "REAL NOT NULL DEFAULT 0")):
            if name not in columns:
                db.execute(f"ALTER TABLE delivery ADD COLUMN {name} {declaration}")
    # Five bounded SMTP attempts per invocation; no PostgreSQL/API dependency for delivery.
    for _ in range(5):
        with closing(sqlite3.connect(path, timeout=5)) as db, db:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute("SELECT d.name,d.generation,d.kind,d.attempts FROM delivery d WHERE d.status IN ('queued','sending') AND d.next_attempt<=? "
                             "AND (d.kind!='recovery' OR NOT EXISTS (SELECT 1 FROM delivery o WHERE o.name=d.name AND o.generation=d.generation "
                             "AND o.kind='P0_open' AND o.status IN ('queued','sending'))) ORDER BY d.created LIMIT 1", (now,)).fetchone()
            if not row:
                break
            name, generation, kind, attempts = row
            if kind == "recovery":
                opening = db.execute("SELECT status FROM delivery WHERE name=? AND generation=? AND kind='P0_open'", (name, generation)).fetchone()
                if not opening or opening[0] != "sent":
                    db.execute("UPDATE delivery SET status='suppressed',action='Opening notification was not accepted; recovery suppressed.' WHERE name=? AND generation=? AND kind=?", (name, generation, kind))
                    continue
            attempts += 1
            db.execute("UPDATE delivery SET status='sending',attempts=?,next_attempt=? WHERE name=? AND generation=? AND kind=?", (attempts, now + 300, name, generation, kind))
        try:
            if attempts > 3:
                raise RuntimeError("Delivery retry budget exhausted.")
            sender(settings, name, generation, kind)
            status, action = "sent", "SMTP server accepted the notification; inbox delivery is not guaranteed."
        except Exception:
            # SMTP errors can contain private addresses/credentials. Never print/store their contents.
            status = "failed" if attempts >= 3 else "queued"
            action = "Delivery failed; retry budget exhausted." if status == "failed" else "Delivery unavailable; queued for retry."
        with closing(sqlite3.connect(path, timeout=5)) as db, db:
            db.execute("UPDATE delivery SET status=?,action=?,next_attempt=? WHERE name=? AND generation=? AND kind=? AND status='sending' AND attempts=?",
                       (status, action, now + min(900, 60 * 2 ** attempts), name, generation, kind, attempts))
    with closing(sqlite3.connect(path, timeout=5)) as db:
        rows = db.execute("SELECT name,generation,kind,status FROM delivery ORDER BY created DESC,name DESC,generation DESC,kind DESC LIMIT 50").fetchall()
        return [dict(zip(("incident", "generation", "kind", "status"), row)) for row in reversed(rows)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--state-dir", type=Path, default=Path(__file__).resolve().parents[2] / ".local-operations")
    parser.add_argument("--allow-loopback-http", action="store_true")
    parser.add_argument("--simulate", choices=["down", "healthy"])
    parser.add_argument("--send-email", action="store_true")
    parser.add_argument("--allow-loopback-smtp", action="store_true")
    args = parser.parse_args()
    parsed = urlsplit(args.base_url)
    loopback = parsed.hostname in ("localhost", "127.0.0.1", "::1")
    if parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path not in ("", "/") or not parsed.hostname or not (
        parsed.scheme == "https" or parsed.scheme == "http" and loopback and args.allow_loopback_http):
        parser.error("Use a configured HTTPS origin, or explicitly allow loopback HTTP for local tests.")
    if args.send_email and args.simulate:
        parser.error("Simulated incidents must never send live email.")
    if args.allow_loopback_smtp and (not args.send_email or not loopback or not args.allow_loopback_http):
        parser.error("Plaintext SMTP is restricted to explicitly selected local loopback tests.")
    try:
        settings = settings_from_env(args.allow_loopback_smtp) if args.send_email else None
    except ValueError as error:
        parser.error(str(error))
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
    path = args.state_dir.resolve() / ("uptime-live.sqlite3" if args.send_email else "uptime-simulation.sqlite3")
    result = store(path, states, recipients, send_email=args.send_email)
    if settings:
        result = deliver(path, settings)
    with closing(sqlite3.connect(path, timeout=5)) as db:
        delivery_problems = db.execute("SELECT count(*) FROM delivery WHERE status IN ('queued','sending','failed')").fetchone()[0]
    print(json.dumps({"healthy": live and ready is True, "simulationOnly": not args.send_email,
                      "notificationPendingOrFailed": delivery_problems, "deliveries": result[-50:]}))
    return 0 if live and ready is True and delivery_problems == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
