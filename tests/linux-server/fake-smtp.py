#!/usr/bin/env python3
"""A tiny SMTP server for the Linux test box: accepts every message without TLS or login and saves it.

    python3 fake-smtp.py --dir /var/lib/pairnets-e2e/mail [--host 127.0.0.1] [--port 15025]

Each message's DATA (dot-unstuffed, CRLF line ends as sent) goes to <dir>/<unix-ms>-<n>.eml, written
under a temporary name first so a reader never sees half a message. Standard library only.
"""

import argparse
import itertools
import os
import socketserver
import sys
import threading
import time

COUNTER = itertools.count(1)
COUNTER_LOCK = threading.Lock()


class Handler(socketserver.StreamRequestHandler):
    timeout = 120

    def reply(self, line: str) -> None:
        self.wfile.write((line + "\r\n").encode("ascii"))
        self.wfile.flush()

    def handle(self) -> None:
        self.reply("220 fake ESMTP")
        while True:
            raw = self.rfile.readline()
            if not raw:
                return
            command = raw.decode("utf-8", "replace").rstrip("\r\n")
            upper = command.upper()
            if upper.startswith("EHLO") or upper.startswith("HELO"):
                self.reply("250 fake")
            elif upper == "DATA":
                self.reply("354 go on")
                self.receive_data()
                self.reply("250 queued")
            elif upper == "QUIT":
                self.reply("221 bye")
                return
            else:  # MAIL FROM, RCPT TO, RSET, NOOP, ...
                self.reply("250 OK")

    def receive_data(self) -> None:
        lines = []
        while True:
            raw = self.rfile.readline()
            if not raw:
                break
            stripped = raw.rstrip(b"\r\n")
            if stripped == b".":
                break
            if raw.startswith(b".."):
                raw = raw[1:]
            lines.append(raw)
        with COUNTER_LOCK:
            n = next(COUNTER)
        name = "%d-%d.eml" % (int(time.time() * 1000), n)
        folder = self.server.mail_dir
        temp = os.path.join(folder, "." + name + ".tmp")
        with open(temp, "wb") as f:
            f.write(b"".join(lines))
        os.chmod(temp, 0o644)
        os.replace(temp, os.path.join(folder, name))
        sys.stderr.write("saved %s\n" % name)
        sys.stderr.flush()


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=15025)
    parser.add_argument("--dir", required=True)
    args = parser.parse_args()
    os.makedirs(args.dir, exist_ok=True)
    server = Server((args.host, args.port), Handler)
    server.mail_dir = args.dir
    sys.stderr.write("fake SMTP on %s:%d, saving to %s\n" % (args.host, args.port, args.dir))
    sys.stderr.flush()
    server.serve_forever()


if __name__ == "__main__":
    main()
