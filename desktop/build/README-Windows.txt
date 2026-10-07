Myra for Windows
=================

Browse h5ai, Apache and nginx directory listings, search them, stream videos
with the built-in VLC player, and download whole folders with aria2.

Start
-----
1. Extract this folder anywhere, for example C:\Apps\Myra.
2. Double-click Myra.exe.
3. If Windows SmartScreen says "Windows protected your PC", click
   "More info", then "Run anyway". The build is not code-signed.
4. Click "+" next to Sources and enter a directory address,
   for example http://server/movies/.

Included
--------
- libVLC 3.0 (in libvlc\) for playback. A separate VLC install is optional;
  it is only used by the "Open in VLC" button.
- aria2c.exe 1.37.0 (in tools\) for downloads. No separate install is needed.

Where data is stored
--------------------
- Sources, downloads and settings: %APPDATA%\Myra\Myra.json
- Search index and resume points:  %APPDATA%\Myra\LibraryIndex.sqlite
- Downloads (default):             your Downloads folder\Myra

Keyboard (player)
-----------------
Space play/pause - Left/Right seek 10 s (Alt: 3 s, Shift: 60 s)
Up/Down volume - M mute - N next - P previous - F or F11 fullscreen - Esc exit fullscreen

Licences are in licenses\: VLC is LGPL 2.1, aria2 is GPL 2.
