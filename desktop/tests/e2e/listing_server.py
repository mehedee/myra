"""Apache-style table listing (like h5ai's fallback) with HTTP Range support, for end-to-end tests.

Usage: python listing_server.py <folder> <port>
"""
import html, io, os, re, sys, time, urllib.parse
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer

class Handler(SimpleHTTPRequestHandler):
    def list_directory(self, path):
        rows = ['<tr><td><img alt="folder-parent"></td><td><a href="..">Parent Directory</a></td><td></td><td></td></tr>']
        for name in sorted(os.listdir(path)):
            full = os.path.join(path, name)
            folder = os.path.isdir(full)
            href = urllib.parse.quote(name) + ("/" if folder else "")
            size = "" if folder else f"{os.path.getsize(full)/1024:.1f} KB"
            date = time.strftime("%Y-%m-%d %H:%M", time.localtime(os.path.getmtime(full)))
            rows.append(f'<tr><td><img alt="{"folder" if folder else "file"}"></td><td><a href="{href}">{html.escape(name)}</a></td><td>{date}</td><td>{size}</td></tr>')
        body = ("<html><body><table>" + "".join(rows) + "</table></body></html>").encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        return io.BytesIO(body)

    def send_head(self):
        path = self.translate_path(self.path)
        match = re.match(r"bytes=(\d+)-(\d*)$", self.headers.get("Range", ""))
        if os.path.isdir(path) or not os.path.isfile(path) or not match:
            return super().send_head()
        size = os.path.getsize(path)
        start = int(match.group(1))
        end = min(int(match.group(2)) if match.group(2) else size - 1, size - 1)
        if start >= size:
            self.send_error(416)
            return None
        f = open(path, "rb")
        f.seek(start)
        self.send_response(206)
        self.send_header("Content-Type", self.guess_type(path))
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.send_header("Content-Length", str(end - start + 1))
        self.end_headers()
        return io.BytesIO(f.read(end - start + 1))

os.chdir(sys.argv[1])
ThreadingHTTPServer(("127.0.0.1", int(sys.argv[2])), Handler).serve_forever()
