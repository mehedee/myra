"""Creates the end-to-end test media tree. Needs ffmpeg on PATH.

Usage: python make_media.py <output-folder>
"""
import os
import subprocess
import sys

root = sys.argv[1]


def run(*args):
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", *args], check=True)


def clip(path, seconds=3, tone=440):
    path = os.path.join(root, path)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    run("-f", "lavfi", "-i", "testsrc=size=320x180:rate=24", "-f", "lavfi", "-i", f"sine=frequency={tone}",
        "-t", str(seconds), "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac",
        "-metadata:s:a:0", "language=eng", path)


clip("Movies/2025/Arrival.Point.2025.1080p.WEB-DL.mkv")
clip("Movies/2025/Arrival.Point.2025.2160p.BluRay.mkv")
clip("Movies/2026/Dune Messiah (2026) 1080p.mp4")
for episode in ("01", "02", "03", "10"):
    clip(f"TV Series/Some Show/Season 1/Some.Show.S01E{episode}.720p.mkv")
clip("Tracks/Long Clip 2020.mp4", seconds=60, tone=500)
with open(os.path.join(root, "Movies/2025/poster.jpg"), "w") as f:
    f.write("poster")
with open(os.path.join(root, "Movies/readme.txt"), "w") as f:
    f.write("notes")

# French default audio and subtitles, with English alternatives.
work = os.path.join(root, "Tracks")
for language, text in (("fr", "Bonjour"), ("en", "Hello")):
    with open(os.path.join(work, f"{language}.srt.tmp"), "w") as f:
        f.write(f"1\n00:00:00,500 --> 00:00:02,500\n{text}\n")
run("-f", "lavfi", "-i", "testsrc=size=320x180:rate=24", "-f", "lavfi", "-i", "sine=frequency=300",
    "-f", "lavfi", "-i", "sine=frequency=600", "-f", "srt", "-i", os.path.join(work, "fr.srt.tmp"),
    "-f", "srt", "-i", os.path.join(work, "en.srt.tmp"), "-t", "3",
    "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3", "-map", "4",
    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-c:s", "srt",
    "-metadata:s:a:0", "language=fre", "-metadata:s:a:0", "title=French",
    "-metadata:s:a:1", "language=eng", "-metadata:s:a:1", "title=English",
    "-metadata:s:s:0", "language=fre", "-metadata:s:s:1", "language=eng",
    "-disposition:a:0", "default", "-disposition:a:1", "0",
    "-disposition:s:0", "default", "-disposition:s:1", "0",
    os.path.join(work, "TrackDefaults.mkv"))
for language in ("fr", "en"):
    os.remove(os.path.join(work, f"{language}.srt.tmp"))

os.makedirs(os.path.join(root, "Big"), exist_ok=True)
with open(os.path.join(root, "Big", "Large Film 2024.mkv"), "wb") as f:
    f.write(os.urandom(30_000_000))
print("test media ready in", root)
