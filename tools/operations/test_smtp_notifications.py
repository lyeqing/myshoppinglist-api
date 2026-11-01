import os
from contextlib import closing
from pathlib import Path
import smtplib
import sqlite3
import io
import json
import socketserver
from http.server import BaseHTTPRequestHandler, HTTPServer
from threading import Thread
import tempfile
import unittest
from unittest.mock import MagicMock, patch
from smtp_notifications import SmtpSettings, settings_from_env, send_notification
from uptime_probe import store, deliver
import uptime_probe


class NotificationTests(unittest.TestCase):
    def test_plaintext_requires_explicit_loopback_without_credentials(self):
        env = {"MYSHOPPINGLIST_SMTP_HOST": "127.0.0.1", "MYSHOPPINGLIST_SMTP_FROM": "alerts@example.test",
               "MYSHOPPINGLIST_INCIDENT_RECIPIENTS": "owner@example.test", "MYSHOPPINGLIST_MONITOR_ID": "local-test"}
        with patch.dict(os.environ, env, clear=True):
            self.assertFalse(settings_from_env().loopback_plaintext)
            self.assertTrue(settings_from_env(True).loopback_plaintext)
            os.environ["MYSHOPPINGLIST_SMTP_HOST"] = "smtp.example.test"
            with self.assertRaises(ValueError):
                settings_from_env(True)
            os.environ["MYSHOPPINGLIST_SMTP_HOST"] = "127.0.0.1"
            os.environ["MYSHOPPINGLIST_SMTP_USERNAME"] = "fixture"
            os.environ["MYSHOPPINGLIST_SMTP_PASSWORD"] = "fixture-only"
            with self.assertRaises(ValueError):
                settings_from_env(True)

    def test_tls_is_required_and_failure_cannot_fall_back_to_sending(self):
        settings = SmtpSettings("smtp.example.test", 587, "alerts@example.test", ("owner@example.test",))
        client = MagicMock()
        with patch("smtp_notifications.smtplib.SMTP") as smtp:
            smtp.return_value.__enter__.return_value = client
            send_notification(settings, "application_unavailable", 1, "P0_open")
            client.starttls.assert_called_once()
            client.send_message.assert_called_once()
            message = client.send_message.call_args.args[0]
            self.assertNotIn("owner@example.test", message.get_content())
        client = MagicMock()
        client.starttls.side_effect = smtplib.SMTPNotSupportedError("Private fixture error must not be logged")
        with patch("smtp_notifications.smtplib.SMTP") as smtp:
            smtp.return_value.__enter__.return_value = client
            with self.assertRaises(smtplib.SMTPNotSupportedError):
                send_notification(settings, "database_unavailable", 1, "P0_open")
            client.send_message.assert_not_called()

    def test_independent_queue_retries_deduplicates_and_reports_only_accepted_sends(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "live.sqlite3"
            result = store(path, {"application_unavailable": False}, recipients=1, now=1000, send_email=True)
            self.assertEqual("queued", result[0]["status"])
            store(path, {"application_unavailable": False}, recipients=1, now=1001, send_email=True)
            attempts = []
            def sender(*args):
                attempts.append(args)
                if len(attempts) < 3:
                    raise RuntimeError("secret fixture address/password must not be retained")
            self.assertEqual("queued", deliver(path, object(), now=1000, sender=sender)[0]["status"])
            deliver(path, object(), now=1100, sender=sender)
            self.assertEqual(1, len(attempts))
            self.assertEqual("queued", deliver(path, object(), now=1120, sender=sender)[0]["status"])
            self.assertEqual("sent", deliver(path, object(), now=1361, sender=sender)[0]["status"])
            self.assertEqual(3, len(attempts))
            store(path, {"application_unavailable": True}, recipients=1, now=1400, send_email=True)
            result = deliver(path, object(), now=1400, sender=sender)
            self.assertEqual(["sent", "sent"], [r["status"] for r in result])
            with closing(sqlite3.connect(path)) as db:
                self.assertNotIn("secret fixture", str(db.execute("SELECT action FROM delivery").fetchall()))

    def test_exhausted_delivery_remains_failed_and_simulations_are_never_sent(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "live.sqlite3"
            store(path, {"database_unavailable": False}, recipients=1, now=1000, send_email=True)
            def fail(*args):
                raise OSError("fixture transport failure")
            for now in (1000, 1120, 1361):
                result = deliver(path, object(), now=now, sender=fail)
            self.assertEqual("failed", result[0]["status"])
            result = store(path, {"database_unavailable": True}, recipients=1, now=1400, send_email=True)
            self.assertEqual("suppressed", result[-1]["status"])
            result = store(path, {"database_unavailable": False}, recipients=1, now=1401, send_email=True)
            self.assertEqual("queued", result[-1]["status"])  # A failed send must not consume the successful-send cooldown.
            simulated = Path(directory) / "simulation.sqlite3"
            store(simulated, {"application_unavailable": False}, recipients=1, now=1000)
            sender = MagicMock()
            result = deliver(simulated, object(), now=1000, sender=sender)
            sender.assert_not_called(); self.assertEqual("simulated", result[0]["status"])

    def test_actual_probe_can_send_a_local_smtp_alert_without_postgresql(self):
        messages = []
        class Receiver(socketserver.StreamRequestHandler):
            def handle(self):
                self.connection.settimeout(5)
                self.wfile.write(b"220 fixture ESMTP\r\n")
                while line := self.rfile.readline():
                    if line.startswith(b"ehlo") or line.startswith(b"helo"):
                        self.wfile.write(b"250 fixture\r\n")
                    elif line.lower().startswith((b"mail from:", b"rcpt to:")):
                        self.wfile.write(b"250 accepted\r\n")
                    elif line.lower().strip() == b"data":
                        self.wfile.write(b"354 data\r\n"); data = []
                        while content := self.rfile.readline():
                            if content == b".\r\n":
                                break
                            data.append(content)
                        messages.append(b"".join(data)); self.wfile.write(b"250 accepted locally\r\n")
                    elif line.lower().strip() == b"quit":
                        self.wfile.write(b"221 bye\r\n"); break
                    else:
                        self.wfile.write(b"500 unsupported\r\n")
        class Health(BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200 if self.path == "/health/live" else 503); self.end_headers()
                self.wfile.write(b'{"status":"up"}')
            def log_message(self, *args):
                pass
        smtp = socketserver.TCPServer(("127.0.0.1", 0), Receiver)
        health = HTTPServer(("127.0.0.1", 0), Health)
        threads = [Thread(target=server.serve_forever, daemon=True) for server in (smtp, health)]
        for thread in threads:
            thread.start()
        try:
            with tempfile.TemporaryDirectory() as directory:
                env = {"MYSHOPPINGLIST_SMTP_HOST": "127.0.0.1", "MYSHOPPINGLIST_SMTP_PORT": str(smtp.server_address[1]),
                       "MYSHOPPINGLIST_SMTP_FROM": "alerts@example.test", "MYSHOPPINGLIST_INCIDENT_RECIPIENTS": "owner@example.test",
                       "MYSHOPPINGLIST_MONITOR_ID": "local-test"}
                args = ["uptime_probe", "--base-url", f"http://127.0.0.1:{health.server_port}", "--allow-loopback-http",
                        "--send-email", "--allow-loopback-smtp", "--state-dir", directory]
                output = io.StringIO()
                with patch.dict(os.environ, env, clear=True), patch("sys.argv", args), patch("sys.stdout", output):
                    self.assertEqual(1, uptime_probe.main())  # Database readiness is down, although sending succeeds.
                result = json.loads(output.getvalue())
                self.assertFalse(result["simulationOnly"]); self.assertEqual("sent", result["deliveries"][0]["status"])
                self.assertEqual(1, len(messages)); self.assertIn(b"database_unavailable", messages[0])
        finally:
            for server in (smtp, health):
                server.shutdown(); server.server_close()
            for thread in threads:
                thread.join()

    def test_simulate_cannot_be_combined_with_live_sending(self):
        with patch("sys.argv", ["uptime_probe", "--base-url", "https://example.invalid", "--simulate", "down", "--send-email"]), patch("sys.stderr", io.StringIO()):
            with self.assertRaises(SystemExit) as error:
                uptime_probe.main()
            self.assertEqual(2, error.exception.code)


if __name__ == "__main__":
    unittest.main()
