## Developer Notes

&nbsp;
&nbsp;

### 2026 revival — architecture of the YouTube Music layer

The app now has **two interchangeable YouTube Music backends**:

1. **Native (C#)** — the original hand-rolled `HttpWebRequest` implementation in
   `YTMusicUploader\Providers\Requests*`, updated in 2026 to the current protocol:
   - `clientVersion` is date-derived (`1.<yyyyMMdd>.01.00`) — Google rejects the old static `0.1` since Sept 2022 (HTTP 404 "Requested entity was not found").
   - `X-Goog-Visitor-Id` is fetched from music.youtube.com's `ytcfg` `VISITOR_DATA` at runtime and cached per session (hardcoded visitor IDs expire).
   - The `SAPISIDHASH` Authorization is derived from the `__Secure-3PAPISID` cookie (some accounts no longer receive a plain `SAPISID`).
   - Responses use gzip/deflate auto-decompression (Brotli is no longer advertised); the `SOCS=CAI` consent cookie is appended.
   - Request contexts were trimmed to the minimal modern shape (`Requests.SetDynamicContext` patches them at runtime).
   - `GetPlaylist` supports both the 2024+ two-column response layout with body-token continuations and the legacy shape.
   - The API key in `App.config` is still valid — current ytmusicapi sends the identical key for browser auth.

2. **Python bridge (preferred when available)** — `Providers\BridgeService.cs` spawns
   `PythonBridge\ytm_bridge.py` (JSON-lines over stdio), which hosts
   [sigma67's ytmusicapi](https://github.com/sigma67/ytmusicapi). Each public `Requests.*`
   method tries the bridge first (`Global.PreferPythonBridge` +
   `BridgeService.TryEnsureSession`) and falls back to native if no usable Python runtime
   is found. Rationale: when Google changes the protocol again, `pip install -U ytmusicapi`
   absorbs it — no C# rework needed.
   - Provision a self-contained runtime with `PythonBridge\Setup-PythonBridge.ps1`
     (downloads the Python embeddable package to `.\Python` and installs ytmusicapi).
   - Uploads through the bridge reimplement the resumable upload flow in Python (using
     ytmusicapi's authenticated session headers) so bandwidth throttling and progress
     reporting keep working.

**Default backend is native** (`PreferPythonBridge=false`). Native is complete and
dependency-free; the bridge is opt-in for its future-proofing. Known bridge-only
limitations when opted in (native is unaffected):
- The uploads *search* (ytmusicapi) returns a videoId but no entityId; the
  already-uploaded check recovers the entityId from the artist cache when possible.
- Whole-album delete relies on ytmusicapi exposing the album's browse id as its entity
  id; where it doesn't, delete a whole album track-by-track instead.
- Bridge calls are serialised (one ytmusicapi instance, not thread-safe), so a long
  upload blocks other bridge operations until it finishes. Native runs them concurrently.

**Auth** is unchanged for users: paste the music.youtube.com cookie into the connect
dialog. Only cookie ("browser") auth can upload — ytmusicapi's OAuth mode cannot.

**Protocol reference:** when the native layer breaks again, diff against
ytmusicapi's source (`constants.py`, `helpers.py`, `ytmusic.py`, `mixins/uploads.py`,
`mixins/playlists.py`) — the native code deliberately mirrors it.

**Building:** requires the .NET Framework 4.7.2 Developer Pack (or pass
`/p:TargetFrameworkRootPath=` pointing at extracted `Microsoft.NETFramework.ReferenceAssemblies.net472`)
and a NuGet restore with `msbuild /t:Restore /p:RestorePackagesConfig=true`.

&nbsp;
&nbsp;

## TODO

&nbsp;
&nbsp;

### Bug Fixes

- Check we're not keep getting 'object reference not set to an instance of an object' error on playlist upload.

&nbsp;
&nbsp;

### Development

- Verify the native `GetPlaylist` parsers against live responses (two-column layout paths were implemented from ytmusicapi's parser source, not live traffic).
- Consider shipping the embedded Python runtime in the installer so the bridge is always available.
