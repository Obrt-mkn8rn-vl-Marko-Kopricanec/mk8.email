#!/usr/bin/env python3
import argparse
import base64
import imaplib
import poplib
import smtplib
import ssl
import subprocess
import time
import uuid
from email import policy
from email.message import EmailMessage
from email.parser import BytesParser
from pathlib import Path


LOCAL_HOST = "127.0.0.1"
INBOUND_HOST = "@@MK8_SERVER_IPV4@@"
DOMAIN = "mk8n.com"
ADMIN = f"admin@{DOMAIN}"
PRIMARY = f"mk8n@{DOMAIN}"
ENCRYPTED_EICAR_ZIP = base64.b64decode(
    "UEsDBC0ACQAAALcQJF08z1Fo//////////8BABQALQEAEABEAAAAAAAAAFAAAAAAAAAA"
    "Cy18tiyQY8pkaPOWeZ96AV8BDLdCpeSUktp1qzQN+oGuoGyYzqnJUwO/UQGHJZZzy"
    "dM+K5JEVl7csRrxLiWNmvRQr/c4RGBHqBdLIdryz9NQSwcIPM9RaFAAAAAAAAAARA"
    "AAAAAAAABQSwECHgMtAAkAAAC3ECRdPM9RaFAAAABEAAAAAQAAAAAAAAABAAAAgBEA"
    "AAAALVBLBgYsAAAAAAAAAB4DLQAAAAAAAAAAAAEAAAAAAAAAAQAAAAAAAAAvAAAAAA"
    "AAAJsAAAAAAAAAUEsGBwAAAADKAAAAAAAAAAEAAABQSwUGAAAAAAEAAQAvAAAAmwAA"
    "AAAA"
)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def tls_context() -> ssl.SSLContext:
    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    return context


def contains_marker(raw: bytes, marker: str) -> bool:
    expected = f"X-Mk8-Test: {marker}".encode("ascii")
    return expected.lower() in raw.lower()


def message(recipient: str, marker: str, body: str = "Local production smoke test.") -> EmailMessage:
    value = EmailMessage()
    value["From"] = "probe@debian.org"
    value["To"] = recipient
    value["Subject"] = f"mk8.email smoke {marker}"
    value["X-Mk8-Test"] = marker
    value.set_content(body)
    return value


def send_inbound(value: EmailMessage) -> None:
    for attempt in range(3):
        try:
            with smtplib.SMTP(INBOUND_HOST, 25, timeout=30) as client:
                client.ehlo("probe.debian.org")
                client.send_message(value)
            return
        except smtplib.SMTPRecipientsRefused as error:
            temporary = all(400 <= result[0] < 500 for result in error.recipients.values())
            if not temporary or attempt == 2:
                raise
            time.sleep(2)


def send_submission(value: EmailMessage, password: str, implicit_tls: bool) -> None:
    value.replace_header("From", ADMIN)
    if implicit_tls:
        client = smtplib.SMTP_SSL(LOCAL_HOST, 465, timeout=30, context=tls_context())
    else:
        client = smtplib.SMTP(LOCAL_HOST, 587, timeout=30)
    with client:
        client.ehlo("probe.debian.org")
        if not implicit_tls:
            client.starttls(context=tls_context())
            client.ehlo("probe.debian.org")
        client.login(ADMIN, password)
        client.send_message(value)


def wait_for_message(
    account: str,
    password: str,
    marker: str,
    folder: str = "INBOX",
    delete: bool = True,
) -> bytes:
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        with imaplib.IMAP4_SSL(LOCAL_HOST, 993, ssl_context=tls_context(), timeout=15) as client:
            client.login(account, password)
            status, _ = client.select(folder)
            require(status == "OK", f"IMAP could not select {folder} for {account}.")
            status, data = client.uid("SEARCH", None, "HEADER", "X-Mk8-Test", marker)
            require(status == "OK", f"IMAP search failed for {account}.")
            identifiers = data[0].split()
            if identifiers:
                identifier = identifiers[-1]
                status, content = client.uid("FETCH", identifier, "(BODY.PEEK[])")
                require(status == "OK", f"IMAP fetch failed for {account}.")
                raw = next(item[1] for item in content if isinstance(item, tuple))
                require(
                    contains_marker(raw, marker),
                    "IMAP header search returned an unrelated message.",
                )
                if delete:
                    client.uid("STORE", identifier, "+FLAGS.SILENT", "(\\Deleted)")
                    client.expunge()
                return raw
        time.sleep(1)
    raise RuntimeError(f"The expected message did not reach {account}.")


def wait_for_subject(account: str, password: str, subject: str) -> None:
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        with imaplib.IMAP4_SSL(LOCAL_HOST, 993, ssl_context=tls_context(), timeout=15) as client:
            client.login(account, password)
            status, _ = client.select("INBOX")
            require(status == "OK", f"IMAP could not select INBOX for {account}.")
            status, data = client.uid("SEARCH", None, "HEADER", "Subject", subject)
            require(status == "OK", f"IMAP subject search failed for {account}.")
            for identifier in reversed(data[0].split()):
                status, content = client.uid("FETCH", identifier, "(BODY.PEEK[])")
                require(status == "OK", f"IMAP fetch failed for {account}.")
                raw = next(item[1] for item in content if isinstance(item, tuple))
                parsed = BytesParser(policy=policy.default).parsebytes(raw)
                if str(parsed.get("Subject", "")).casefold() != subject.casefold():
                    continue
                client.uid("STORE", identifier, "+FLAGS.SILENT", "(\\Deleted)")
                client.expunge()
                return
        time.sleep(1)
    raise RuntimeError(f"The message with the expected subject did not reach {account}.")


def wait_for_pop3_message(
    account: str,
    password: str,
    marker: str,
    implicit_tls: bool,
) -> bytes:
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        client: poplib.POP3 | None = None
        try:
            if implicit_tls:
                client = poplib.POP3_SSL(
                    LOCAL_HOST,
                    995,
                    timeout=15,
                    context=tls_context(),
                )
            else:
                client = poplib.POP3(LOCAL_HOST, 110, timeout=15)
                clear_capabilities = client.capa()
                require(b"STLS" in clear_capabilities, "POP3 did not advertise STLS.")
                client.stls(context=tls_context())

            capabilities = client.capa()
            require(b"UIDL" in capabilities, "POP3 did not advertise UIDL.")
            require(b"TOP" in capabilities, "POP3 did not advertise TOP.")
            client.user(account)
            client.pass_(password)
            _, uidl_lines, _ = client.uidl()
            unique_ids = [line.split(maxsplit=1)[1] for line in uidl_lines]
            require(
                len(unique_ids) == len(set(unique_ids)),
                "POP3 returned duplicate UIDLs.",
            )
            _, message_lines, _ = client.list()
            for listing in reversed(message_lines):
                number = int(listing.split(maxsplit=1)[0])
                _, header_lines, _ = client.top(number, 0)
                headers = b"\r\n".join(header_lines) + b"\r\n"
                if not contains_marker(headers, marker):
                    continue
                _, content_lines, _ = client.retr(number)
                raw = b"\r\n".join(content_lines) + b"\r\n"
                require(contains_marker(raw, marker), "POP3 TOP and RETR returned different messages.")
                client.dele(number)
                client.quit()
                client = None
                return raw
        finally:
            if client is not None:
                try:
                    client.quit()
                except poplib.error_proto:
                    client.close()
        time.sleep(1)
    raise RuntimeError(f"The expected POP3 message did not reach {account}.")


def require_absent(account: str, password: str, marker: str) -> None:
    with imaplib.IMAP4_SSL(LOCAL_HOST, 993, ssl_context=tls_context(), timeout=15) as client:
        client.login(account, password)
        client.select("INBOX")
        status, data = client.uid("SEARCH", None, "HEADER", "X-Mk8-Test", marker)
        require(status == "OK", f"IMAP search failed for {account}.")
        identifiers = data[0].split()
        for identifier in identifiers:
            status, content = client.uid("FETCH", identifier, "(BODY.PEEK[])")
            require(status == "OK", f"IMAP fetch failed for {account}.")
            raw = next(item[1] for item in content if isinstance(item, tuple))
            require(
                not contains_marker(raw, marker),
                "A rejected message reached a mailbox.",
            )
        require(not identifiers, "IMAP header search returned an unrelated message.")


def queue_status(marker: str) -> tuple[str, int]:
    require(marker.isascii() and marker.isalnum(), "The queue marker is not safe.")
    query = (
        "SELECT state || '|' || attempt_count FROM mail_queue_messages "
        f"WHERE raw_message LIKE '%{marker}%' ORDER BY received_at DESC LIMIT 1"
    )
    result = subprocess.run(
        [
            "runuser",
            "-u",
            "postgres",
            "--",
            "psql",
            "--dbname=mk8email",
            "--no-psqlrc",
            "--tuples-only",
            "--no-align",
            "--command",
            query,
        ],
        check=True,
        capture_output=True,
        text=True,
        timeout=15,
    )
    output = result.stdout.strip()
    if not output:
        return "", 0
    state, separator, attempts = output.partition("|")
    require(separator == "|" and attempts.isdecimal(), "The queue status is not valid.")
    return state, int(attempts)


def wait_for_queue_state(
    marker: str,
    expected: str,
    timeout: int = 90,
    minimum_attempts: int = 0,
) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        state, attempts = queue_status(marker)
        if state == expected and attempts >= minimum_attempts:
            return
        time.sleep(1)
    raise RuntimeError(f"The queue message did not enter the {expected} state.")


def delete_queue_message(marker: str) -> None:
    require(marker.isascii() and marker.isalnum(), "The queue marker is not safe.")
    query = f"DELETE FROM mail_queue_messages WHERE raw_message LIKE '%{marker}%'"
    subprocess.run(
        [
            "runuser",
            "-u",
            "postgres",
            "--",
            "psql",
            "--dbname=mk8email",
            "--no-psqlrc",
            "--quiet",
            "--command",
            query,
        ],
        check=True,
        timeout=15,
    )


def test_open_relay() -> None:
    with smtplib.SMTP(INBOUND_HOST, 25, timeout=30) as client:
        client.ehlo("probe.debian.org")
        require(client.mail("probe@debian.org")[0] == 250, "The relay test sender was not accepted.")
        code, _ = client.rcpt("recipient@debian.org")
        require(code in (550, 554), "mk8.email accepted an unauthenticated relay recipient.")


def test_sender_mismatch(password: str) -> None:
    with smtplib.SMTP(LOCAL_HOST, 587, timeout=30) as client:
        client.ehlo("probe.debian.org")
        client.starttls(context=tls_context())
        client.ehlo("probe.debian.org")
        client.login(ADMIN, password)
        code, _ = client.mail(PRIMARY)
        if code < 400:
            code, _ = client.rcpt(ADMIN)
        require(code in (550, 553), "mk8.email accepted an unauthorized sender identity.")


def baseline(admin_password: str, primary_password: str) -> None:
    admin_marker = uuid.uuid4().hex
    send_inbound(message(ADMIN, admin_marker))
    wait_for_message(ADMIN, admin_password, admin_marker)

    catchall_marker = uuid.uuid4().hex
    send_inbound(message(f"undefined-{catchall_marker}@{DOMAIN}", catchall_marker))
    wait_for_message(PRIMARY, primary_password, catchall_marker)

    pop3s_marker = uuid.uuid4().hex
    send_inbound(message(ADMIN, pop3s_marker))
    wait_for_pop3_message(ADMIN, admin_password, pop3s_marker, implicit_tls=True)

    pop3_stls_marker = uuid.uuid4().hex
    send_inbound(message(ADMIN, pop3_stls_marker))
    wait_for_pop3_message(ADMIN, admin_password, pop3_stls_marker, implicit_tls=False)

    starttls_marker = uuid.uuid4().hex
    send_submission(message(ADMIN, starttls_marker), admin_password, implicit_tls=False)
    raw = wait_for_message(ADMIN, admin_password, starttls_marker)
    require(b"DKIM-Signature:" in raw, "The STARTTLS submission did not receive a DKIM signature.")

    implicit_marker = uuid.uuid4().hex
    send_submission(message(ADMIN, implicit_marker), admin_password, implicit_tls=True)
    wait_for_message(ADMIN, admin_password, implicit_marker)

    test_open_relay()
    test_sender_mismatch(admin_password)
    print("Baseline SMTP, submission, IMAP, POP3, catch-all, DKIM, and relay tests passed.")


def unsafe_content(admin_password: str) -> None:
    eicar_marker = uuid.uuid4().hex
    eicar = message(ADMIN, eicar_marker)
    eicar.add_attachment(
        b"X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*",
        maintype="application",
        subtype="octet-stream",
        filename="eicar.com",
    )
    try:
        send_inbound(eicar)
        wait_for_queue_state(eicar_marker, "quarantined")
        require_absent(ADMIN, admin_password, eicar_marker)
    finally:
        delete_queue_message(eicar_marker)

    gtube_marker = uuid.uuid4().hex
    gtube = message(
        ADMIN,
        gtube_marker,
        "XJS*C4JDBQADN1.NSBN3*2IDNEN*GTUBE-STANDARD-ANTI-UBE-TEST-EMAIL*C.34X",
    )
    send_inbound(gtube)
    wait_for_message(ADMIN, admin_password, gtube_marker, folder="Spam")

    encrypted_marker = uuid.uuid4().hex
    encrypted = message(ADMIN, encrypted_marker)
    encrypted.add_attachment(
        ENCRYPTED_EICAR_ZIP,
        maintype="application",
        subtype="zip",
        filename="encrypted-eicar.zip",
    )
    try:
        send_inbound(encrypted)
        wait_for_queue_state(encrypted_marker, "quarantined")
        require_absent(ADMIN, admin_password, encrypted_marker)
    finally:
        delete_queue_message(encrypted_marker)
    print("EICAR, GTUBE, and encrypted archive rejection tests passed.")


def scanner_unavailable() -> str:
    marker = uuid.uuid4().hex
    value = message(ADMIN, marker, f"Scanner availability probe {marker}.")
    value.add_attachment(
        marker.encode("ascii"),
        maintype="application",
        subtype="octet-stream",
        filename=f"{marker}.bin",
    )
    send_inbound(value)
    wait_for_queue_state(marker, "pending", minimum_attempts=1)
    print(marker)
    return marker


def send_queue_probe() -> str:
    marker = uuid.uuid4().hex
    send_inbound(message(ADMIN, marker))
    print(marker)
    return marker


def receive_queue_probe(admin_password: str, marker: str) -> None:
    wait_for_message(ADMIN, admin_password, marker)
    wait_for_queue_state(marker, "completed")
    print("The persisted queue message was delivered after service recovery.")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "mode",
        choices=("baseline", "unsafe", "scanner-down", "queue-send", "queue-receive", "subject-receive"),
    )
    parser.add_argument("--admin-password-file", default="/etc/mk8email/bootstrap-secrets/admin.password")
    parser.add_argument("--primary-password-file", default="/etc/mk8email/bootstrap-secrets/mk8n.password")
    parser.add_argument("--account")
    parser.add_argument("--marker")
    parser.add_argument("--subject")
    arguments = parser.parse_args()

    if arguments.mode == "baseline":
        admin_password = Path(arguments.admin_password_file).read_text(encoding="ascii")
        primary_password = Path(arguments.primary_password_file).read_text(encoding="ascii")
        baseline(admin_password, primary_password)
    elif arguments.mode == "unsafe":
        admin_password = Path(arguments.admin_password_file).read_text(encoding="ascii")
        unsafe_content(admin_password)
    elif arguments.mode == "scanner-down":
        scanner_unavailable()
    elif arguments.mode == "queue-send":
        send_queue_probe()
    elif arguments.mode == "subject-receive":
        require(arguments.account is not None, "The account is required.")
        require(arguments.subject is not None, "The subject is required.")
        admin_password = Path(arguments.admin_password_file).read_text(encoding="ascii")
        wait_for_subject(arguments.account, admin_password, arguments.subject)
        print("The message was delivered and removed through native IMAP.")
    else:
        require(arguments.marker is not None, "The queue marker is required.")
        admin_password = Path(arguments.admin_password_file).read_text(encoding="ascii")
        receive_queue_probe(admin_password, arguments.marker)


if __name__ == "__main__":
    main()
