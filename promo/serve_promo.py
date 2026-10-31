#!/usr/bin/env python3
"""Static HTTP server with full HTTP 206 Byte-Range support for smooth <video> seeking in Live Preview."""
import os
import re
import sys
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer

ROOT = os.path.dirname(os.path.abspath(__file__))


class RangeHTTPRequestHandler(SimpleHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=ROOT, **kwargs)

    def end_headers(self):
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Access-Control-Allow-Origin", "*")
        super().end_headers()

    def send_head(self):
        path = self.translate_path(self.path)
        if os.path.isdir(path):
            return super().send_head()
        if not os.path.exists(path):
            self.send_error(404, "File not found")
            return None

        range_header = self.headers.get("Range")
        if not range_header:
            return super().send_head()

        m = re.match(r"bytes=(\d*)-(\d*)", range_header)
        if not m:
            return super().send_head()

        file_size = os.path.getsize(path)
        start = int(m.group(1)) if m.group(1) else 0
        end = int(m.group(2)) if m.group(2) else file_size - 1
        end = min(end, file_size - 1)
        if start > end or start >= file_size:
            self.send_error(416, "Requested Range Not Satisfiable")
            return None

        length = end - start + 1
        ctype = self.guess_type(path)
        f = open(path, "rb")
        f.seek(start)

        self.send_response(206)
        self.send_header("Content-type", ctype)
        self.send_header("Content-Range", f"bytes {start}-{end}/{file_size}")
        self.send_header("Content-Length", str(length))
        self.send_header("Last-Modified", self.date_time_string(os.path.getmtime(path)))
        self.end_headers()
        self._range_remaining = length
        return f

    def copyfile(self, source, outputfile):
        remaining = getattr(self, "_range_remaining", None)
        if remaining is None:
            return super().copyfile(source, outputfile)
        buf_size = 64 * 1024
        while remaining > 0:
            chunk = source.read(min(buf_size, remaining))
            if not chunk:
                break
            outputfile.write(chunk)
            remaining -= len(chunk)
        self._range_remaining = None


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8000
    server = ThreadingHTTPServer(("0.0.0.0", port), RangeHTTPRequestHandler)
    print(f"Serving {ROOT} on 0.0.0.0:{port} with HTTP 206 Range support", flush=True)
    server.serve_forever()
