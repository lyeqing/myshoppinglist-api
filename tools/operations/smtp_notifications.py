"""Mandatory-TLS SMTP for the independently hosted uptime probe. Disabled without explicit opt-in."""
from dataclasses import dataclass
from email.message import EmailMessage
from email.utils import parseaddr
import hashlib
import ipaddress
import os
import re
import smtplib
import ssl


@dataclass(frozen=True)
class SmtpSettings:
    host: str
    port: int
    sender: str
    recipients: tuple[str, ...]
    username: str = ""
    password: str = ""
    implicit_tls: bool = False
    loopback_plaintext: bool = False
    monitor_id: str = "myshoppinglist"


def settings_from_env(allow_loopback=False):
    host = os.getenv("MYSHOPPINGLIST_SMTP_HOST", "")
    sender = os.getenv("MYSHOPPINGLIST_SMTP_FROM", "")
    recipients = tuple(r.strip() for r in os.getenv("MYSHOPPINGLIST_INCIDENT_RECIPIENTS", "").split(",") if r.strip())
    username, password = os.getenv("MYSHOPPINGLIST_SMTP_USERNAME", ""), os.getenv("MYSHOPPINGLIST_SMTP_PASSWORD", "")
    monitor = os.getenv("MYSHOPPINGLIST_MONITOR_ID", "")
    tls = os.getenv("MYSHOPPINGLIST_SMTP_IMPLICIT_TLS", "false").lower()
    try:
        port = int(os.getenv("MYSHOPPINGLIST_SMTP_PORT", "587"))
        loopback = ipaddress.ip_address(host).is_loopback
    except ValueError:
        loopback = False
        try:
            port = int(os.getenv("MYSHOPPINGLIST_SMTP_PORT", "587"))
        except ValueError:
            raise ValueError("Configure a valid SMTP port.") from None

    def address(value):
        return 0 < len(value) <= 320 and not any(c.isspace() or ord(c) < 32 for c in value) and parseaddr(value)[1] == value and "@" in value and "." in value.rsplit("@", 1)[1]

    if not host or len(host) > 253 or any(c.isspace() or c in "/\\@?#" for c in host) or not 1 <= port <= 65535:
        raise ValueError("Configure a valid SMTP host and port.")
    if not address(sender) or not 1 <= len(recipients) <= 5 or not all(address(r) for r in recipients):
        raise ValueError("Configure a valid sender and one to five private recipients.")
    if not re.fullmatch(r"[A-Za-z0-9._-]{1,64}", monitor):
        raise ValueError("Configure a stable monitor identifier for this environment.")
    if bool(username) != bool(password) or len(username) > 320 or len(password) > 4096 or tls not in ("true", "false"):
        raise ValueError("Configure matching SMTP credentials and an explicit TLS mode.")
    if allow_loopback and (not loopback or username or password or tls == "true"):
        raise ValueError("Plaintext SMTP is limited to credential-free, explicitly selected loopback tests.")
    return SmtpSettings(host, port, sender, recipients, username, password, tls == "true", allow_loopback, monitor)


def send_notification(settings, name, generation, kind):
    # Callers use fixed monitor names. Do not put URLs, recipient addresses, or exception text in messages/logs.
    if name not in ("application_unavailable", "database_unavailable") or kind not in ("P0_open", "recovery") or generation < 1:
        raise ValueError("Use a fixed uptime incident definition.")
    reference = hashlib.sha256(f"{settings.monitor_id}:{name}:{generation}:{kind}".encode()).hexdigest()[:24]
    message = EmailMessage()
    message["From"] = settings.sender
    message["To"] = settings.recipients[0] if len(settings.recipients) == 1 else "undisclosed-recipients:;"
    message["Subject"] = f"MyShoppingList uptime: {kind}"
    message["Message-ID"] = f"<uptime-{reference}@{settings.sender.rsplit('@', 1)[1]}>"
    message.set_content(f"Incident reference: uptime-{reference}\nMonitor: {settings.monitor_id}\nSignal: {name}\nTransition: {kind}\n"
                        "Check the independent network, application host and database. Follow the protected operations runbook.\n"
                        "Delivery may be delayed; check current health before acting. This notification contains no customer data.\n")
    factory = smtplib.SMTP_SSL if settings.implicit_tls else smtplib.SMTP
    kwargs = {"timeout": 10}
    if settings.implicit_tls:
        kwargs["context"] = ssl.create_default_context()
    with factory(settings.host, settings.port, **kwargs) as client:
        client.ehlo()
        if not settings.implicit_tls and not settings.loopback_plaintext:
            client.starttls(context=ssl.create_default_context())  # Never opportunistic/fallback TLS.
            client.ehlo()
        if settings.username:
            client.login(settings.username, settings.password)
        client.send_message(message, from_addr=settings.sender, to_addrs=settings.recipients)
