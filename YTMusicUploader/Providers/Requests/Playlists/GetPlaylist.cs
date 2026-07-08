using JBToolkit.StreamHelpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Net;
using YTMusicUploader.Providers.DataModels;
using YTMusicUploader.Providers.RequestModels;
using static YTMusicUploader.Providers.RequestModels.ArtistCache;

namespace YTMusicUploader.Providers
{
    /// <summary>
    /// YouTube Music API Request Methods
    ///
    /// Thanks to: sigma67:
    ///     https://ytmusicapi.readthedocs.io/en/latest/
    ///     https://github.com/sigma67/ytmusicapi
    /// </summary>
    public partial class Requests
    {
        /// <summary>
        /// YouTube Music API request methods specifically for playlist manipulation
        /// </summary>
        public partial class Playlists
        {
            /// <summary>
            /// HttpWebRequest POST request to send to YTM, which gets a playlist given a playlist or browse id
            /// (You can get this from the 'Requests.Playlists.GetPlaylists (plural)') request method. And then recurisvely
            /// fetches all tracks listed in the playlist
            /// </summary>
            /// <param name="cookieValue">Cookie from a previous YouTube Music sign in via this application (stored in the database)</param>
            /// <param name="browseId">Playlist id or browse id to retreive the playlist for</param>
            /// <param name="playlist">Shuuld be null initially. This object is used for recursive purposes</param>
            /// <param name="continuationToken">Should be null initially. This object is used for recursive purposes.</param>
            /// <returns>OnlinePlaylist object</returns>
            public static OnlinePlaylist GetPlaylist(
            string cookieValue,
            string browseId,
            OnlinePlaylist playlist = null,
            string continuationToken = null)
            {
                // Preferred path: the Python bridge (ytmusicapi) - it returns the complete track
                // listing in one shot, so it's only used on a fresh (non-recursive) request
                if (playlist == null &&
                    string.IsNullOrEmpty(continuationToken) &&
                    Global.PreferPythonBridge &&
                    BridgeService.TryEnsureSession(cookieValue))
                {
                    try
                    {
                        return GetPlaylistViaBridge(browseId);
                    }
                    catch (BridgeUnavailableException)
                    {
                        // Bridge process died - fall through to the native HttpWebRequest implementation
                    }
                    catch (BridgeException e)
                    {
                        // Return null (not an empty playlist) on a hard failure. An empty
                        // OnlinePlaylist would be read downstream as 'the playlist exists but has no
                        // tracks', causing every track to be re-added and duplicated; null makes the
                        // caller skip this playlist for the current pass instead
                        var _ = e;
#if DEBUG
                        Console.Out.WriteLine("GetPlaylist (bridge): " + e.Message);
#endif
                        return null;
                    }
                }

                return GetPlaylist(cookieValue, browseId, playlist, continuationToken, false);
            }

            /// <summary>
            /// Fetches a playlist (complete with track listing) via the Python bridge (ytmusicapi)
            /// and maps it onto the same OnlinePlaylist shape the native parser produces. The privacy
            /// status isn't reported by the bridge and is defaulted to Private, as the native parser
            /// does when it can't be determined
            /// </summary>
            /// <param name="browseId">Playlist id or browse id to retrieve the playlist for</param>
            /// <returns>OnlinePlaylist object</returns>
            private static OnlinePlaylist GetPlaylistViaBridge(string browseId)
            {
                // The bridge (ytmusicapi) expects the raw playlist id, without the 'VL' browse prefix
                string playlistId = !string.IsNullOrEmpty(browseId) && browseId.StartsWith("VL")
                                            ? browseId.Substring(2, browseId.Length - 2)
                                            : browseId;

                var result = BridgeService.Invoke("get_playlist", new JObject
                {
                    ["playlistId"] = playlistId
                });

                var playlist = new OnlinePlaylist
                {
                    // Callers expect the 'VL'-prefixed browse id, exactly as the native
                    // implementation sets it before sending the 'browse' request
                    BrowseId = "VL" + playlistId,
                    Title = (string)result["title"],
                    Description = (string)result["description"] ?? string.Empty,
                    Duration = (string)result["duration"],
                    CoverArtUrl = (string)result["coverUrl"],

                    // The bridge doesn't report the privacy status - default to Private,
                    // as the native parser does when it can't be determined
                    PrivacyStatus = OnlinePlaylist.PrivacyStatusEmum.Private,
                    Songs = new PlaylistSongCollection()
                };

                if (result["tracks"] is JArray tracks)
                {
                    foreach (var track in tracks)
                    {
                        playlist.Songs.Add(new PlaylistSong
                        {
                            Title = (string)track["title"],
                            ArtistTitle = (string)track["artist"] ?? string.Empty,
                            AlbumTitle = (string)track["album"] ?? string.Empty,
                            Duration = (string)track["duration"],
                            VideoId = (string)track["videoId"] ?? string.Empty,
                            SetVideoId = (string)track["setVideoId"] ?? string.Empty,
                            CoverArtUrl = (string)track["coverUrl"] ?? string.Empty
                        });
                    }
                }

                return playlist;
            }

            /// <summary>
            /// Internal implementation of GetPlaylist. YouTube Music now has two continuation styles:
            /// the classic one where the token goes in the URL ('ctoken' / 'continuation' query string
            /// parameters) and the 2025 one where the token is sent in the POST body as a 'continuation'
            /// field - 'continuationTokenInBody' indicates which style the supplied token requires.
            /// </summary>
            private static OnlinePlaylist GetPlaylist(
            string cookieValue,
            string browseId,
            OnlinePlaylist playlist,
            string continuationToken,
            bool continuationTokenInBody)
            {
                if (playlist == null)
                    playlist = new OnlinePlaylist();

                if (playlist.Songs == null)
                    playlist.Songs = new PlaylistSongCollection();

                try
                {
                    // The 'browse' endpoint requires the playlist id to be prefixed with 'VL'
                    if (!string.IsNullOrEmpty(browseId) && !browseId.StartsWith("VL"))
                        browseId = "VL" + browseId;

                    bool tokenInUrl = !string.IsNullOrEmpty(continuationToken) && !continuationTokenInBody;

                    var request = (HttpWebRequest)WebRequest.Create(Global.YTMusicBaseUrl +
                                                                    "browse" +
                                                                    (!tokenInUrl
                                                                                    ? ""
                                                                                    : "?ctoken=" + continuationToken +
                                                                                      "&continuation=" + continuationToken) +
                                                                    (!tokenInUrl
                                                                                    ? Global.YTMusicParams
                                                                                    : Global.YTMusicParams.Replace('?', '&')));
                    request = AddStandardHeaders(request, cookieValue);
                    request = AddApiHeaders(request, cookieValue);

                    var context = JsonConvert.DeserializeObject<BrowseArtistRequestContext>(
                                    SafeFileStream.ReadAllText(
                                                        Path.Combine(
                                                                Global.WorkingDirectory,
                                                                @"AppData\get_playlist_context.json")));

                    context.browseId = string.Format("{0}", browseId);

                    string body = SetDynamicContext(JsonConvert.SerializeObject(
                                                        context,
                                                        Formatting.None,
                                                        new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));

                    if (continuationTokenInBody && !string.IsNullOrEmpty(continuationToken))
                    {
                        // 2025 continuation protocol: the token is sent in the POST body, not the URL
                        var bodyObject = JObject.Parse(body);
                        bodyObject["continuation"] = continuationToken;
                        body = bodyObject.ToString();
                    }

                    byte[] postBytes = GetPostBytes(body);
                    request.ContentLength = postBytes.Length;

                    using (var requestStream = request.GetRequestStream())
                    {
                        requestStream.Write(postBytes, 0, postBytes.Length);
                        requestStream.Close();
                    }

                    postBytes = null;
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        string result = ReadResponseBody(response);

                        if (string.IsNullOrEmpty(continuationToken))
                        {
                            playlist.BrowseId = browseId;
                            SetPlaylistDetails(playlist, result);

                            playlist.Songs = GetInitalPlaylistSongs(playlist.Songs, result, out string continuation, out bool tokenInBody);
                            if (!string.IsNullOrEmpty(continuation))
                                return GetPlaylist(cookieValue, browseId, playlist, continuation, tokenInBody);
                        }
                        else
                        {
                            playlist.Songs = GetContinuationPlaylistSongs(playlist.Songs, result, out string continuation, out bool tokenInBody);
                            if (!string.IsNullOrEmpty(continuation))
                                return GetPlaylist(cookieValue, browseId, playlist, continuation, tokenInBody);
                        }
                    }
                }
                catch (Exception e)
                {
                    var _ = e;
#if DEBUG
                    Console.Out.WriteLine("GetPlaylist: " + e.Message);
#endif
                    // If the very first request failed (nothing fetched yet), return null so the
                    // caller skips this playlist rather than treating an empty result as a real,
                    // trackless playlist and duplicating every track into it. A failure part-way
                    // through continuations still returns what was gathered so far
                    if (string.IsNullOrEmpty(continuationToken) && playlist.Songs.Count == 0)
                        return null;
                }

                return playlist;
            }

            /// <summary>
            /// Sets the playlist header details (title, subtitle, description, duration, cover art and
            /// privacy status) from the browse response. Since 2024 the header is a
            /// 'musicResponsiveHeaderRenderer' within the new two column layout (nested inside a
            /// 'musicEditablePlaylistDetailHeaderRenderer' for playlists the user owns). The pre-2024
            /// 'musicDetailHeaderRenderer' shape is kept as a fallback.
            /// </summary>
            private static void SetPlaylistDetails(OnlinePlaylist playlist, string result)
            {
                var jo = JObject.Parse(result);

                JToken header = jo.Descendants().Where(t => t.Type == JTokenType.Property && ((JProperty)t).Name == "musicResponsiveHeaderRenderer")
                                                .Select(p => ((JProperty)p).Value)
                                                .FirstOrDefault();

                if (header == null)
                {
                    // Legacy layout fallback
                    header = jo.Descendants().Where(t => t.Type == JTokenType.Property && ((JProperty)t).Name == "musicDetailHeaderRenderer")
                                             .Select(p => ((JProperty)p).Value)
                                             .FirstOrDefault();
                }

                if (header != null)
                {
                    try
                    {
                        playlist.Title = (string)header.SelectToken("title.runs[0].text");
                    }
                    catch { }

                    try
                    {
                        var subtitleRuns = header.SelectToken("subtitle.runs") as JArray;
                        if (subtitleRuns != null)
                        {
                            string subtitle = string.Empty;
                            foreach (var run in subtitleRuns)
                                subtitle += (string)run["text"];

                            playlist.Subtitle = subtitle;
                        }
                    }
                    catch { }

                    try
                    {
                        // The duration is the last run of the second subtitle (i.e. '25 songs • 1 hour, 15 minutes')
                        var secondSubtitleRuns = header.SelectToken("secondSubtitle.runs") as JArray;
                        if (secondSubtitleRuns != null && secondSubtitleRuns.Count > 1)
                            playlist.Duration = (string)secondSubtitleRuns[secondSubtitleRuns.Count - 1]["text"];
                    }
                    catch { }

                    try
                    {
                        playlist.Description =
                            ((string)header.SelectToken("description.musicDescriptionShelfRenderer.description.runs[0].text") ??
                             (string)header.SelectToken("description.runs[0].text") ??
                             (string)jo.SelectTokens("$..musicPlaylistEditHeaderRenderer.description.runs[0].text").FirstOrDefault()) ??
                            "";
                    }
                    catch
                    {
                        playlist.Description = "";
                    }

                    try
                    {
                        playlist.CoverArtUrl =
                            (string)header.SelectToken("thumbnail.musicThumbnailRenderer.thumbnail.thumbnails[0].url") ??
                            (string)header.SelectToken("thumbnail.croppedSquareThumbnailRenderer.thumbnail.thumbnails[0].url");
                    }
                    catch { }
                }

                try
                {
                    string privacy = (string)jo.SelectTokens("$..musicPlaylistEditHeaderRenderer.privacy").FirstOrDefault();
                    playlist.PrivacyStatus = (OnlinePlaylist.PrivacyStatusEmum)Enum.Parse(
                                                typeof(OnlinePlaylist.PrivacyStatusEmum),
                                                privacy,
                                                true);
                }
                catch
                {
                    playlist.PrivacyStatus = OnlinePlaylist.PrivacyStatusEmum.Private;
                }
            }

            private static PlaylistSongCollection GetInitalPlaylistSongs(
                PlaylistSongCollection playlistSongCollection,
                string result,
                out string continuation,
                out bool continuationTokenInBody)
            {
                continuation = string.Empty;
                continuationTokenInBody = false;

                var jo = JObject.Parse(result);
                var musicShelfRendererTokens = jo.Descendants().Where(t => t.Type == JTokenType.Property && ((JProperty)t).Name == "musicPlaylistShelfRenderer")
                                                               .Select(p => ((JProperty)p).Value).ToList();

                foreach (var token in musicShelfRendererTokens)
                {
                    var msr = token.ToObject<BrowsePlaylistResultsContext.Musicplaylistshelfrenderer>();
                    if (msr == null || msr.contents == null)
                        continue;

                    if (msr.continuations != null &&
                        msr.continuations.Length > 0 &&
                        msr.continuations[0].nextContinuationData != null &&
                        msr.continuations[0].nextContinuationData.continuation != null)
                    {
                        continuation = msr.continuations[0].nextContinuationData.continuation;
                    }

                    foreach (var content in msr.contents)
                    {
                        var song = CreatePlaylistSong(content);
                        if (song != null)
                            playlistSongCollection.Add(song);
                    }
                }

                // 2025 continuation protocol: the token lives in a trailing 'continuationItemRenderer'
                // item and must be sent in the POST body of the next request
                var newStyleToken = jo.SelectTokens("$..continuationItemRenderer.continuationEndpoint.continuationCommand.token")
                                      .FirstOrDefault();

                if (newStyleToken != null)
                {
                    continuation = (string)newStyleToken;
                    continuationTokenInBody = true;
                }

                return playlistSongCollection;
            }

            private static PlaylistSongCollection GetContinuationPlaylistSongs(
                PlaylistSongCollection playlistSongCollection,
                string result,
                out string continuation,
                out bool continuationTokenInBody)
            {
                continuation = string.Empty;
                continuationTokenInBody = false;

                var jo = JObject.Parse(result);

                // Legacy continuation shape: response.continuationContents.musicPlaylistShelfContinuation
                var musicShelfRendererTokens = jo.Descendants().Where(t => t.Type == JTokenType.Property && ((JProperty)t).Name == "musicPlaylistShelfContinuation")
                                                               .Select(p => ((JProperty)p).Value).ToList();

                foreach (var token in musicShelfRendererTokens)
                {
                    var msr = token.ToObject<BrowsePlaylistResultsContext.Musicplaylistshelfrenderer>();
                    if (msr == null || msr.contents == null)
                        continue;

                    if (msr.continuations != null &&
                        msr.continuations.Length > 0 &&
                        msr.continuations[0].nextContinuationData != null &&
                        msr.continuations[0].nextContinuationData.continuation != null)
                    {
                        continuation = msr.continuations[0].nextContinuationData.continuation;
                    }

                    foreach (var content in msr.contents)
                    {
                        var song = CreatePlaylistSong(content);
                        if (song != null)
                            playlistSongCollection.Add(song);
                    }
                }

                // 2025 continuation shape: the next page of tracks arrives under
                // onResponseReceivedActions[0].appendContinuationItemsAction.continuationItems
                var appendActionTokens = jo.Descendants().Where(t => t.Type == JTokenType.Property && ((JProperty)t).Name == "appendContinuationItemsAction")
                                                         .Select(p => ((JProperty)p).Value).ToList();

                foreach (var token in appendActionTokens)
                {
                    var items = token["continuationItems"] as JArray;
                    if (items == null)
                        continue;

                    foreach (var item in items)
                    {
                        BrowsePlaylistResultsContext.Content2 content = null;
                        try
                        {
                            content = item.ToObject<BrowsePlaylistResultsContext.Content2>();
                        }
                        catch { }

                        var song = CreatePlaylistSong(content);
                        if (song != null)
                            playlistSongCollection.Add(song);
                    }
                }

                // 2025 continuation protocol: the token lives in a trailing 'continuationItemRenderer'
                // item and must be sent in the POST body of the next request
                var newStyleToken = jo.SelectTokens("$..continuationItemRenderer.continuationEndpoint.continuationCommand.token")
                                      .FirstOrDefault();

                if (newStyleToken != null)
                {
                    continuation = (string)newStyleToken;
                    continuationTokenInBody = true;
                }

                return playlistSongCollection;
            }

            /// <summary>
            /// Creates a PlaylistSong from a 'musicResponsiveListItemRenderer' playlist entry. Returns
            /// null if the entry isn't a track (i.e. it's a trailing 'continuationItemRenderer') or if
            /// it couldn't be parsed.
            /// </summary>
            private static PlaylistSong CreatePlaylistSong(BrowsePlaylistResultsContext.Content2 content)
            {
                if (content == null ||
                    content.musicResponsiveListItemRenderer == null ||
                    content.musicResponsiveListItemRenderer.fixedColumns == null)
                {
                    return null;
                }

                try
                {
                    var renderer = content.musicResponsiveListItemRenderer;

                    string coverArtUrl = string.Empty;
                    try
                    {
                        coverArtUrl = renderer.thumbnail
                                              .musicThumbnailRenderer
                                              .thumbnail
                                              .thumbnails[0].url;
                    }
                    catch { }

                    var song = new PlaylistSong
                    {
                        Title = renderer.flexColumns[0]
                                        .musicResponsiveListItemFlexColumnRenderer
                                        .text
                                        .runs[0]
                                        .text,

                        ArtistTitle = renderer.flexColumns.Length > 1 &&
                                      renderer.flexColumns[1].musicResponsiveListItemFlexColumnRenderer.text.runs != null
                                            ? renderer.flexColumns[1]
                                                      .musicResponsiveListItemFlexColumnRenderer
                                                      .text
                                                      .runs[0]
                                                      .text
                                            : "",

                        AlbumTitle = renderer.flexColumns.Length > 2 &&
                                     renderer.flexColumns[2].musicResponsiveListItemFlexColumnRenderer.text.runs != null
                                            ? renderer.flexColumns[2]
                                                      .musicResponsiveListItemFlexColumnRenderer
                                                      .text
                                                      .runs[0]
                                                      .text
                                            : "",

                        Duration = renderer.fixedColumns[0]
                                           .musicResponsiveListItemFixedColumnRenderer
                                           .text
                                           .runs[0]
                                           .text,

                        VideoId = renderer.playlistItemData != null &&
                                  !string.IsNullOrEmpty(renderer.playlistItemData.videoId)
                                        ? renderer.playlistItemData.videoId
                                        : GetVideoEntityId(renderer.menu != null
                                                                ? renderer.menu.menuRenderer
                                                                : null),

                        SetVideoId = renderer.playlistItemData != null &&
                                     !string.IsNullOrEmpty(renderer.playlistItemData.playlistSetVideoId)
                                        ? renderer.playlistItemData.playlistSetVideoId
                                        : GetSetVideoEntityId(renderer.menu != null
                                                                ? renderer.menu.menuRenderer
                                                                : null),

                        CoverArtUrl = coverArtUrl
                    };

                    return song;
                }
                catch (Exception e)
                {
                    var _ = e;
#if DEBUG
                    Console.Out.WriteLine(e.Message);
#endif
                    Logger.Log(e, "CreatePlaylistSong - Error fetching playlist items", Log.LogTypeEnum.Error);
                    return null;
                }
            }
        }

        private static string GetVideoEntityId(BrowsePlaylistResultsContext.Menurenderer menuRenderer)
        {
            if (menuRenderer == null || menuRenderer.items == null)
                return string.Empty;

            foreach (var item in menuRenderer.items)
            {
                if (item.menuServiceItemRenderer != null)
                {
                    try
                    {
                        // The 'remove from playlist' menu entry is the one whose playlistEditEndpoint
                        // has a 'removedVideoId' action (structural check - the menu text is locale dependent)
                        if (item.menuServiceItemRenderer.serviceEndpoint != null &&
                            item.menuServiceItemRenderer.serviceEndpoint.playlistEditEndpoint != null &&
                            item.menuServiceItemRenderer.serviceEndpoint.playlistEditEndpoint.actions != null &&
                            item.menuServiceItemRenderer.serviceEndpoint.playlistEditEndpoint.actions.Length > 0 &&
                            !string.IsNullOrEmpty(item.menuServiceItemRenderer.serviceEndpoint.playlistEditEndpoint.actions[0].removedVideoId))
                        {
                            return item.menuServiceItemRenderer
                                       .serviceEndpoint
                                       .playlistEditEndpoint
                                       .actions[0].removedVideoId;
                        }
                    }
                    catch { }
                }
            }

            return string.Empty;
        }

        private static string GetSetVideoEntityId(BrowsePlaylistResultsContext.Menurenderer menuRenderer)
        {
            if (menuRenderer == null || menuRenderer.items == null)
                return string.Empty;

            foreach (var item in menuRenderer.items)
            {
                if (item.menuServiceItemRenderer != null)
                {
                    try
                    {
                        if (item.menuServiceItemRenderer.serviceEndpoint != null &&
                            item.menuServiceItemRenderer.serviceEndpoint.playlistEditEndpoint != null &&
                            item.menuServiceItemRenderer.serviceEndpoint.playlistEditEndpoint.actions != null &&
                            item.menuServiceItemRenderer.serviceEndpoint.playlistEditEndpoint.actions.Length > 0 &&
                            !string.IsNullOrEmpty(item.menuServiceItemRenderer.serviceEndpoint.playlistEditEndpoint.actions[0].setVideoId))
                        {
                            return item.menuServiceItemRenderer
                                       .serviceEndpoint
                                       .playlistEditEndpoint
                                       .actions[0].setVideoId;
                        }
                    }
                    catch { }
                }
            }

            return string.Empty;
        }
    }
}
