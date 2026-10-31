import json
import tempfile
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from threading import Thread
from uptime_probe import check, store


class ProbeTests(unittest.TestCase):
    def test_outage_deduplication_recovery_and_cooldown_without_api_or_database(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state.sqlite3"
            store(path, {"application_unavailable": False}, 1, now=1000)
            store(path, {"application_unavailable": False}, 1, now=1001)
            result = store(path, {"application_unavailable": True}, 1, now=1002)
            self.assertEqual([r["kind"] for r in result], ["P0_open", "recovery"])
            self.assertTrue(all(r["status"] == "simulated" for r in result))
            result = store(path, {"application_unavailable": False}, 1, now=1003)
            self.assertEqual(result[-1]["status"], "suppressed")
            result = store(path, {"application_unavailable": True}, 1, now=1004)
            self.assertEqual(result[-1]["status"], "suppressed")

    def test_absent_recipient_is_not_a_sent_notification(self):
        with tempfile.TemporaryDirectory() as directory:
            result = store(Path(directory) / "state.sqlite3", {"database_unavailable": False})
            self.assertEqual(result[0]["status"], "not_configured")

    def test_real_probe_checks_status_body_and_does_not_follow_redirect_as_success(self):
        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                if self.path == "/redirect":
                    self.send_response(302); self.send_header("Location", "/health/live"); self.end_headers(); return
                self.send_response(200); self.send_header("Content-Type", "application/json"); self.end_headers()
                self.wfile.write(json.dumps({"status": "up" if self.path == "/health/live" else "not_ready"}).encode())
            def log_message(self, *args):
                pass
        server = HTTPServer(("127.0.0.1", 0), Handler)
        thread = Thread(target=server.serve_forever, daemon=True); thread.start()
        try:
            base = "http://127.0.0.1:" + str(server.server_port)
            self.assertTrue(check(base, "/health/live", "up"))
            self.assertFalse(check(base, "/health/ready", "ready"))
            self.assertFalse(check(base, "/redirect", "up"))
        finally:
            server.shutdown(); server.server_close(); thread.join()


if __name__ == "__main__":
    unittest.main()
