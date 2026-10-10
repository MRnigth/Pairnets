#!/usr/bin/env python3
"""A stand-in for Google's sign-in, for the Linux test box (rules: docs/PREDEPLOY.md, "The fake Google").

    python3 fake-google.py [--host 127.0.0.1] [--port 15480]

GET /auth redirects straight back to the nest with a code that carries the identity, the nonce and the
PKCE challenge; POST /token checks the client and the PKCE verifier and answers an identity token (its
signature is three dummy bytes: the nest does not check it for a token endpoint that is not Google's).
Standard library only.
"""

import argparse
import base64
import hashlib
import json
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlencode, urlsplit

CLIENT_ID = "client-123.apps.googleusercontent.com"
CLIENT_SECRET = "test-oauth-secret"
DEFAULT_SUB = "e2e-google-user"
DEFAULT_EMAIL = "owner@example.com"


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def b64url_decode(text: str) -> bytes:
    return base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))


def compact(value) -> bytes:
    return json.dumps(value, separators=(",", ":")).encode("utf-8")


class Handler(BaseHTTPRequestHandler):
    server_version = "fake-google"
    sys_version = ""

    def log_message(self, fmt, *args):  # one line per request on stderr (the journal)
        sys.stderr.write("%s %s\n" % (self.command, self.path.split("?")[0]))

    def send_json(self, status: int, value) -> None:
        body = compact(value)
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        url = urlsplit(self.path)
        if url.path != "/auth":
            self.send_json(404, {"error": "not_found"})
            return
        query = {k: v[0] for k, v in parse_qs(url.query, keep_blank_values=True).items()}
        redirect_uri = query.get("redirect_uri", "")
        if not redirect_uri:
            self.send_json(400, {"error": "invalid_request", "error_description": "redirect_uri is missing"})
            return
        hint = query.get("login_hint", "")
        if hint:
            sub, email = "e2e-" + hint.split("@", 1)[0], hint
        else:
            sub, email = DEFAULT_SUB, DEFAULT_EMAIL
        code = b64url(compact({
            "sub": sub,
            "email": email,
            "nonce": query.get("nonce", ""),
            "challenge": query.get("code_challenge", ""),
        }))
        separator = "&" if "?" in redirect_uri else "?"
        location = redirect_uri + separator + urlencode({"state": query.get("state", ""), "code": code})
        self.send_response(302)
        self.send_header("Location", location)
        self.send_header("Content-Length", "0")
        self.send_header("Cache-Control", "no-store")
        self.end_headers()

    def do_POST(self):
        if urlsplit(self.path).path != "/token":
            self.send_json(404, {"error": "not_found"})
            return
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length).decode("utf-8", "replace") if length > 0 else ""
        form = {k: v[0] for k, v in parse_qs(raw, keep_blank_values=True).items()}
        if (form.get("client_id") != CLIENT_ID or form.get("client_secret") != CLIENT_SECRET
                or form.get("grant_type") != "authorization_code"):
            self.send_json(401, {"error": "invalid_client"})
            return
        try:
            claims_in = json.loads(b64url_decode(form.get("code", "")))
            if not isinstance(claims_in, dict):
                raise ValueError("code is not an object")
        except (ValueError, TypeError):
            self.send_json(400, {"error": "invalid_grant"})
            return
        verifier = form.get("code_verifier", "")
        if b64url(hashlib.sha256(verifier.encode("ascii", "replace")).digest()) != claims_in.get("challenge"):
            self.send_json(400, {"error": "invalid_grant"})
            return
        header = {"alg": "RS256", "typ": "JWT"}
        claims = {
            "iss": self.server.base_url,
            "aud": CLIENT_ID,
            "sub": claims_in.get("sub", ""),
            "email": claims_in.get("email", ""),
            "email_verified": True,
            "nonce": claims_in.get("nonce", ""),
            "exp": int(time.time()) + 300,
        }
        id_token = ".".join([b64url(compact(header)), b64url(compact(claims)), b64url(bytes([1, 2, 3]))])
        self.send_json(200, {"id_token": id_token, "access_token": "unused", "token_type": "Bearer"})


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=15480)
    args = parser.parse_args()
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    server.daemon_threads = True
    server.base_url = "http://%s:%d" % (args.host, args.port)
    sys.stderr.write("fake Google on %s\n" % server.base_url)
    sys.stderr.flush()
    server.serve_forever()


if __name__ == "__main__":
    main()
