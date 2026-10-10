#!/usr/bin/env python3
import argparse
import http.cookiejar
import ssl
import urllib.error
import urllib.parse
import urllib.request
from html.parser import HTMLParser
from pathlib import Path

# This is an operator template, not a configured target.
import re
if re.search(r"[@]{2}MK8_[A-Z0-9_]+[@]{2}", Path(__file__).read_text(encoding="utf-8")):
    raise RuntimeError("Render required deployment inputs before using this template.")



class TokenParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.token = None

    def handle_starttag(self, tag: str, attributes: list[tuple[str, str | None]]) -> None:
        values = dict(attributes)
        if tag == "input" and values.get("name") == "__RequestVerificationToken":
            self.token = values.get("value")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--password-file", default="/etc/mk8email/bootstrap-secrets/@@MK8_ADMIN_LOCAL_PART@@.password")
    arguments = parser.parse_args()
    password = Path(arguments.password_file).read_text(encoding="ascii")

    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    cookies = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(
        urllib.request.HTTPCookieProcessor(cookies),
        urllib.request.HTTPSHandler(context=context),
    )
    base = "https://@@MK8_SERVER_IPV4@@:8443"

    with opener.open(f"{base}/Login", timeout=15) as response:
        login_page = response.read().decode("utf-8")
    token_parser = TokenParser()
    token_parser.feed(login_page)
    require(token_parser.token is not None, "The login page did not contain an antiforgery token.")

    form = urllib.parse.urlencode(
        {
            "Input.Username": "@@MK8_ADMIN_LOCAL_PART@@@@@MK8_PRIMARY_DOMAIN@@",
            "Input.Password": password,
            "ReturnUrl": "/",
            "__RequestVerificationToken": token_parser.token,
        }
    ).encode("ascii")
    request = urllib.request.Request(f"{base}/Login", data=form, method="POST")
    with opener.open(request, timeout=30) as response:
        status_page = response.read().decode("utf-8")
    require("Mail service status" in status_page, "The administrator login did not reach the status page.")
    require("Operational health" in status_page, "The dashboard did not show operational health.")
    require("Healthy" in status_page, "The dashboard did not show a healthy current snapshot.")
    require("Queued messages" in status_page, "The dashboard did not show the mail queue metric.")
    require("ClamAV signature age" in status_page, "The dashboard did not show signature freshness.")

    session_cookie = next((cookie for cookie in cookies if cookie.name == "__Host-mk8admin"), None)
    require(session_cookie is not None and session_cookie.secure, "The secure administrator cookie is missing.")

    with opener.open(f"{base}/Accounts", timeout=15) as response:
        accounts_page = response.read().decode("utf-8")
    require("@@MK8_ADMIN_LOCAL_PART@@@@@MK8_PRIMARY_DOMAIN@@" in accounts_page, "The administrator account is missing from the dashboard.")
    require("@@MK8_PRIMARY_LOCAL_PART@@@@@MK8_PRIMARY_DOMAIN@@" in accounts_page, "The primary account is missing from the dashboard.")

    try:
        opener.open(urllib.request.Request(f"{base}/Logout", data=b"", method="POST"), timeout=15)
    except urllib.error.HTTPError as error:
        require(error.code == 400, "A logout request without an antiforgery token returned an unexpected status.")
    else:
        raise RuntimeError("The dashboard accepted a request without an antiforgery token.")

    print("Dashboard health, login, account visibility, secure cookie, and antiforgery tests passed.")


if __name__ == "__main__":
    main()
