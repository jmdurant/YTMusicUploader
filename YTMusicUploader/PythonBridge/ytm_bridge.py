#!/usr/bin/env python3
"""JSON-lines stdio bridge between YTMusicUploader (C#) and ytmusicapi.

Protocol
--------
Request  (stdin):  {"id": <int>, "cmd": "<name>", "args": {...}}
Response (stdout): {"id": <int>, "ok": true,  "result": <any>}
                   {"id": <int>, "ok": false, "error": "<msg>", "errorType": "<ExceptionClass>"}
Progress (stdout, upload_song only):
                   {"event": "progress", "id": <request id>, "sent": <bytes>, "total": <bytes>}

stdout carries protocol JSON exclusively, one object per line, flushed after
every write. All diagnostics go to stderr. Requires Python 3.10+.
"""

from __future__ import annotations

import json
import os
import sys
import time

# ---------------------------------------------------------------------------
# Stream setup: force UTF-8 on Windows, keep stdout clean for protocol JSON.
# ---------------------------------------------------------------------------
for _stream in (sys.stdin, sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:  # pragma: no cover - non-reconfigurable stream
        pass


def _emit(obj: dict) -> None:
    """Write one protocol object to stdout as a single flushed JSON line."""
    sys.stdout.write(json.dumps(obj, ensure_ascii=False, default=str) + "\n")
    sys.stdout.flush()


def _log(msg: str) -> None:
    sys.stderr.write(f"[ytm_bridge] {msg}\n")
    sys.stderr.flush()


# ---------------------------------------------------------------------------
# Errors
# ---------------------------------------------------------------------------
class UnknownCommand(Exception):
    pass


class NotInitialized(Exception):
    pass


class UploadError(Exception):
    pass


# ---------------------------------------------------------------------------
# Global state
# ---------------------------------------------------------------------------
YT = None  # the authenticated ytmusicapi.YTMusic instance, set by "init"

UPLOAD_LIMIT_BYTES = 314572800  # 300 MB
SUPPORTED_EXTENSIONS = {".mp3", ".m4a", ".wma", ".flac", ".ogg", ".oga"}
UPLOAD_CHUNK_SIZE = 262144  # 256 KB


def _require_yt():
    if YT is None:
        raise NotInitialized("not initialized: call 'init' with a cookie first")
    return YT


# ---------------------------------------------------------------------------
# Mapping helpers (defensive: missing keys -> None)
# ---------------------------------------------------------------------------
def _first_artist(item: dict):
    artists = item.get("artists")
    if isinstance(artists, list) and artists:
        first = artists[0]
        if isinstance(first, dict):
            return first.get("name")
        if isinstance(first, str):
            return first
    if isinstance(artists, str):
        return artists
    artist = item.get("artist")
    if isinstance(artist, str):
        return artist
    return None


def _album_name(item: dict):
    album = item.get("album")
    if isinstance(album, dict):
        return album.get("name")
    if isinstance(album, str):
        return album
    return None


def _album_id(item: dict):
    """The album's browse id. For uploaded albums this doubles as the entity id
    accepted by delete_upload_entity, so the C# side can delete a whole album."""
    album = item.get("album")
    if isinstance(album, dict):
        return album.get("id")
    return None


def _cover_url(item: dict):
    thumbnails = item.get("thumbnails")
    if isinstance(thumbnails, list) and thumbnails:
        last = thumbnails[-1]
        if isinstance(last, dict):
            return last.get("url")
    return None


def _map_upload_song(item: dict) -> dict:
    return {
        "title": item.get("title"),
        "artist": _first_artist(item),
        "album": _album_name(item),
        "albumId": _album_id(item),
        "duration": item.get("duration"),
        "videoId": item.get("videoId"),
        "entityId": item.get("entityId"),
        "coverUrl": _cover_url(item),
    }


# ---------------------------------------------------------------------------
# Command handlers. Each receives (args: dict, request_id) and returns the
# value for "result". Exceptions become ok:false responses.
# ---------------------------------------------------------------------------
def cmd_ping(args, request_id):
    return "pong"


def cmd_init(args, request_id):
    global YT
    import platform

    import ytmusicapi
    from ytmusicapi import YTMusic
    from ytmusicapi.constants import USER_AGENT

    cookie = args.get("cookie")
    if not cookie or not isinstance(cookie, str):
        raise ValueError("init requires a non-empty string 'cookie'")
    auth_user = str(args.get("authUser") or "0")

    headers = {
        "cookie": cookie,
        # Stale placeholder: ytmusicapi detects browser auth by the substring
        # "SAPISIDHASH" and recomputes the real hash on every request.
        "authorization": "SAPISIDHASH placeholder_replaced_per_request",
        "origin": "https://music.youtube.com",
        "x-origin": "https://music.youtube.com",
        "x-goog-authuser": auth_user,
        "user-agent": USER_AGENT,
        "accept": "*/*",
        "content-type": "application/json",
    }

    # Replaces any previous instance (e.g. cookie changed).
    YT = None
    YT = YTMusic(auth=headers)
    _log("initialized YTMusic (authuser=%s)" % auth_user)
    return {
        "pythonVersion": platform.python_version(),
        "ytmusicapiVersion": getattr(ytmusicapi, "__version__", None),
    }


def cmd_is_authenticated(args, request_id):
    yt = _require_yt()
    yt.get_library_upload_albums(limit=1)  # raises on bad/expired credentials
    return True


def cmd_search_uploads(args, request_id):
    yt = _require_yt()
    query = args.get("query")
    if not query or not isinstance(query, str):
        raise ValueError("search_uploads requires a non-empty string 'query'")
    results = yt.search(query, scope="uploads")  # no filter allowed with uploads scope
    mapped = []
    for item in results or []:
        if not isinstance(item, dict):
            continue
        mapped.append(
            {
                "type": item.get("resultType") or item.get("category"),
                "title": item.get("title"),
                "artist": _first_artist(item),
                "album": _album_name(item),
                "videoId": item.get("videoId"),
                "entityId": item.get("entityId"),
                "duration": item.get("duration"),
            }
        )
    return mapped


def cmd_get_upload_artists(args, request_id):
    yt = _require_yt()
    artists = yt.get_library_upload_artists(limit=None)
    return [
        {"name": a.get("artist") or a.get("name"), "browseId": a.get("browseId")}
        for a in artists or []
        if isinstance(a, dict)
    ]


def cmd_get_upload_artist_songs(args, request_id):
    yt = _require_yt()
    browse_id = args.get("browseId")
    if not browse_id or not isinstance(browse_id, str):
        raise ValueError("get_upload_artist_songs requires a non-empty string 'browseId'")
    songs = yt.get_library_upload_artist(browse_id, limit=None)
    return [_map_upload_song(s) for s in songs or [] if isinstance(s, dict)]


def cmd_get_upload_songs(args, request_id):
    yt = _require_yt()
    songs = yt.get_library_upload_songs(limit=None)
    return [_map_upload_song(s) for s in songs or [] if isinstance(s, dict)]


def _throttled_file_reader(path, total, request_id, max_bytes_per_second):
    """Yield ~256KB chunks, throttling and emitting progress events."""
    sent = 0
    started = time.monotonic()
    with open(path, "rb") as fh:
        while True:
            chunk = fh.read(UPLOAD_CHUNK_SIZE)
            if not chunk:
                break
            yield chunk
            sent += len(chunk)
            _emit({"event": "progress", "id": request_id, "sent": sent, "total": total})
            if max_bytes_per_second and max_bytes_per_second > 0:
                expected_elapsed = sent / float(max_bytes_per_second)
                actual_elapsed = time.monotonic() - started
                if expected_elapsed > actual_elapsed:
                    time.sleep(expected_elapsed - actual_elapsed)


def cmd_upload_song(args, request_id):
    import requests

    yt = _require_yt()
    path = args.get("path")
    if not path or not isinstance(path, str):
        raise ValueError("upload_song requires a non-empty string 'path'")
    max_bps = int(args.get("maxBytesPerSecond") or 0)

    if not os.path.isfile(path):
        raise FileNotFoundError(f"file not found: {path}")
    ext = os.path.splitext(path)[1].lower()
    if ext not in SUPPORTED_EXTENSIONS:
        raise UploadError(
            f"unsupported file type '{ext}': YouTube Music accepts "
            + ", ".join(sorted(e.lstrip(".") for e in SUPPORTED_EXTENSIONS))
        )
    total = os.path.getsize(path)
    if total >= UPLOAD_LIMIT_BYTES:
        raise UploadError(
            f"file is {total} bytes, which exceeds the YouTube Music upload limit of 300MB"
        )

    # Reuse the authenticated machinery: yt.headers computes a fresh
    # SAPISIDHASH authorization on every access.
    headers = dict(yt.headers.copy())
    headers.pop("content-encoding", None)
    headers["content-type"] = "application/x-www-form-urlencoded;charset=utf-8"
    authuser = headers.get("x-goog-authuser", "0")
    upload_url = f"https://upload.youtube.com/upload/usermusic/http?authuser={authuser}"

    # Step 1: start a resumable upload session.
    start_headers = dict(headers)
    start_headers["X-Goog-Upload-Command"] = "start"
    start_headers["X-Goog-Upload-Header-Content-Length"] = str(total)
    start_headers["X-Goog-Upload-Protocol"] = "resumable"
    body = ("filename=" + os.path.basename(path)).encode("utf-8")
    response = requests.post(upload_url, data=body, headers=start_headers,
                             proxies=getattr(yt, "proxies", None))
    if response.status_code == 409:
        return {"status": "conflict"}
    if response.status_code != 200:
        raise UploadError(
            f"upload session start failed: HTTP {response.status_code} {response.reason}"
        )
    session_url = response.headers.get("X-Goog-Upload-URL")
    if not session_url:
        raise UploadError("upload session start did not return X-Goog-Upload-URL")

    # Step 2: stream the file bytes, throttled, with progress events.
    upload_headers = dict(headers)
    upload_headers["X-Goog-Upload-Command"] = "upload, finalize"
    upload_headers["X-Goog-Upload-Offset"] = "0"
    response = requests.post(
        session_url,
        data=_throttled_file_reader(path, total, request_id, max_bps),
        headers=upload_headers,
        proxies=getattr(yt, "proxies", None),
    )
    if response.status_code == 409:
        return {"status": "conflict"}
    if response.status_code != 200:
        raise UploadError(f"upload failed: HTTP {response.status_code} {response.reason}")
    return {"status": "ok"}


def cmd_delete_upload_entity(args, request_id):
    yt = _require_yt()
    entity_id = args.get("entityId")
    if not entity_id or not isinstance(entity_id, str):
        raise ValueError("delete_upload_entity requires a non-empty string 'entityId'")
    result = yt.delete_upload_entity(entity_id)
    if "SUCCEEDED" in str(result):
        return True
    raise UploadError(f"delete failed: {result}")


def cmd_get_playlists(args, request_id):
    yt = _require_yt()
    playlists = yt.get_library_playlists(limit=None)
    return [
        {
            "playlistId": p.get("playlistId"),
            "title": p.get("title"),
            "description": p.get("description"),
            "count": p.get("count"),
            "coverUrl": _cover_url(p),
        }
        for p in playlists or []
        if isinstance(p, dict)
    ]


def cmd_get_playlist(args, request_id):
    yt = _require_yt()
    playlist_id = args.get("playlistId")
    if not playlist_id or not isinstance(playlist_id, str):
        raise ValueError("get_playlist requires a non-empty string 'playlistId'")
    limit = args.get("limit")
    playlist = yt.get_playlist(playlist_id, limit=limit or None)
    tracks = []
    for t in playlist.get("tracks") or []:
        if not isinstance(t, dict):
            continue
        tracks.append(
            {
                "videoId": t.get("videoId"),
                "setVideoId": t.get("setVideoId"),
                "title": t.get("title"),
                "artist": _first_artist(t),
                "album": _album_name(t),
                "duration": t.get("duration"),
                "coverUrl": _cover_url(t),
            }
        )
    return {
        "playlistId": playlist.get("id") or playlist_id,
        "title": playlist.get("title"),
        "description": playlist.get("description"),
        "trackCount": playlist.get("trackCount"),
        "duration": playlist.get("duration"),
        "coverUrl": _cover_url(playlist),
        "tracks": tracks,
    }


def cmd_create_playlist(args, request_id):
    yt = _require_yt()
    title = args.get("title")
    if not title or not isinstance(title, str):
        raise ValueError("create_playlist requires a non-empty string 'title'")
    # YT Music rejects '<' and '>' in playlist titles.
    title = title.replace("<", "").replace(">", "")
    description = args.get("description") or ""
    privacy = args.get("privacyStatus") or "PRIVATE"
    result = yt.create_playlist(title, description, privacy_status=privacy)
    if isinstance(result, str):
        return {"playlistId": result}
    if isinstance(result, dict):
        playlist_id = result.get("playlistId")
        if playlist_id:
            return {"playlistId": playlist_id}
        raise UploadError(f"create_playlist failed: {json.dumps(result, default=str)[:500]}")
    raise UploadError(f"create_playlist returned unexpected value: {result!r}")


def cmd_delete_playlist(args, request_id):
    yt = _require_yt()
    playlist_id = args.get("playlistId")
    if not playlist_id or not isinstance(playlist_id, str):
        raise ValueError("delete_playlist requires a non-empty string 'playlistId'")
    yt.delete_playlist(playlist_id)
    return True


def cmd_add_playlist_items(args, request_id):
    yt = _require_yt()
    playlist_id = args.get("playlistId")
    video_ids = args.get("videoIds")
    if not playlist_id or not isinstance(playlist_id, str):
        raise ValueError("add_playlist_items requires a non-empty string 'playlistId'")
    if not isinstance(video_ids, list) or not video_ids:
        raise ValueError("add_playlist_items requires a non-empty list 'videoIds'")
    result = yt.add_playlist_items(playlist_id, video_ids, duplicates=False)
    if isinstance(result, dict):
        status = str(result.get("status", ""))
    else:
        status = str(result)
    if "SUCCEEDED" in status:
        return True
    raise UploadError(f"add_playlist_items failed: {json.dumps(result, default=str)[:500]}")


def cmd_remove_playlist_items(args, request_id):
    yt = _require_yt()
    playlist_id = args.get("playlistId")
    videos = args.get("videos")
    if not playlist_id or not isinstance(playlist_id, str):
        raise ValueError("remove_playlist_items requires a non-empty string 'playlistId'")
    if not isinstance(videos, list) or not videos:
        raise ValueError(
            "remove_playlist_items requires a non-empty list 'videos' of "
            "{videoId, setVideoId} objects"
        )
    result = yt.remove_playlist_items(playlist_id, videos)
    status = str(result.get("status", "")) if isinstance(result, dict) else str(result)
    if "SUCCEEDED" in status:
        return True
    raise UploadError(f"remove_playlist_items failed: {json.dumps(result, default=str)[:500]}")


# Commands allowed before init.
UNGUARDED_COMMANDS = {"ping", "init", "shutdown"}

HANDLERS = {
    "ping": cmd_ping,
    "init": cmd_init,
    "is_authenticated": cmd_is_authenticated,
    "search_uploads": cmd_search_uploads,
    "get_upload_artists": cmd_get_upload_artists,
    "get_upload_artist_songs": cmd_get_upload_artist_songs,
    "get_upload_songs": cmd_get_upload_songs,
    "upload_song": cmd_upload_song,
    "delete_upload_entity": cmd_delete_upload_entity,
    "get_playlists": cmd_get_playlists,
    "get_playlist": cmd_get_playlist,
    "create_playlist": cmd_create_playlist,
    "delete_playlist": cmd_delete_playlist,
    "add_playlist_items": cmd_add_playlist_items,
    "remove_playlist_items": cmd_remove_playlist_items,
}


# ---------------------------------------------------------------------------
# Main loop
# ---------------------------------------------------------------------------
def main() -> int:
    _log("bridge started (python %s)" % sys.version.split()[0])
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        try:
            request = json.loads(line)
            if not isinstance(request, dict):
                raise ValueError("request must be a JSON object")
        except Exception as exc:
            _emit(
                {
                    "id": None,
                    "ok": False,
                    "error": f"malformed request line: {exc}",
                    "errorType": "ProtocolError",
                }
            )
            continue

        request_id = request.get("id")
        cmd = request.get("cmd")
        args = request.get("args") or {}
        if not isinstance(args, dict):
            args = {}

        if cmd == "shutdown":
            _emit({"id": request_id, "ok": True, "result": True})
            _log("shutdown requested, exiting")
            return 0

        try:
            handler = HANDLERS.get(cmd)
            if handler is None:
                raise UnknownCommand(f"unknown command: {cmd!r}")
            if cmd not in UNGUARDED_COMMANDS:
                _require_yt()
            result = handler(args, request_id)
            _emit({"id": request_id, "ok": True, "result": result})
        except Exception as exc:
            _log(f"command {cmd!r} failed: {type(exc).__name__}: {exc}")
            _emit(
                {
                    "id": request_id,
                    "ok": False,
                    "error": str(exc) or type(exc).__name__,
                    "errorType": type(exc).__name__,
                }
            )

    _log("stdin closed (EOF), exiting")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:  # parent killed us; exit quietly
        sys.exit(0)
    except Exception as exc:  # last-resort: never let a traceback hit stdout
        import traceback

        traceback.print_exc(file=sys.stderr)
        sys.stderr.flush()
        sys.exit(1)
