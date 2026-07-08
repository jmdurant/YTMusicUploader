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
            /// HttpWebRequest POST request to send to YTM which fetches a list (collection) of Playlists (without playlist tracks).
            /// Use the 'Requests.Playlists.GetPlaylist (singular)' method to get an individual playlist complete with track listing.
            /// </summary>
            /// <param name="cookieValue">Cookie from a previous YouTube Music sign in via this application (stored in the database)</param>
            /// <returns>OnlinePlaylistCollection object (list of playlists without tracks)</returns>
            public static OnlinePlaylistCollection GetPlaylists(
                string cookieValue,
                string continuationToken = null,
                OnlinePlaylistCollection playListCol = null)
            {
                if (playListCol == null)
                    playListCol = new OnlinePlaylistCollection();

                // Preferred path: the Python bridge (ytmusicapi) - it returns the full library in
                // one shot, so it's only used on the initial call, never for native continuations
                if (string.IsNullOrEmpty(continuationToken) &&
                    Global.PreferPythonBridge &&
                    BridgeService.TryEnsureSession(cookieValue))
                {
                    try
                    {
                        var result = BridgeService.Invoke("get_playlists", new JObject());

                        if (result is JArray bridgePlaylists)
                        {
                            foreach (var bridgePlaylist in bridgePlaylists)
                            {
                                string playlistId = (string)bridgePlaylist["playlistId"];
                                string title = (string)bridgePlaylist["title"];

                                if (string.IsNullOrEmpty(playlistId) || string.IsNullOrEmpty(title))
                                    continue;

                                // The automatic likes playlist must be skipped, as the native parser
                                // does with the 'Your likes' grid tile ('LM' is its fixed id)
                                if (playlistId == "LM" || title == "Your likes" || title == "Liked Music")
                                    continue;

                                playListCol.Add(new OnlinePlaylist
                                {
                                    Title = title,
                                    CoverArtUrl = (string)bridgePlaylist["coverUrl"],

                                    // Callers expect the 'VL'-prefixed browse id, which is what the
                                    // native grid parser reads from the browse endpoint
                                    BrowseId = playlistId.StartsWith("VL")
                                                    ? playlistId
                                                    : "VL" + playlistId
                                });
                            }
                        }

                        return playListCol;
                    }
                    catch (BridgeUnavailableException)
                    {
                        // Bridge process died - fall through to the native HttpWebRequest implementation
                    }
                    catch (BridgeException e)
                    {
                        // Same behaviour as the native failure path: swallow and return what we have
                        var _ = e;
#if DEBUG
                        Console.Out.WriteLine("GetPlaylists (bridge): " + e.Message);
#endif
                        return playListCol;
                    }
                }

                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(Global.YTMusicBaseUrl +
                                                                     "browse" +
                                                                    (string.IsNullOrEmpty(continuationToken)
                                                                                    ? ""
                                                                                    : "?ctoken=" + continuationToken +
                                                                                      "&continuation=" + continuationToken) +
                                                                    (string.IsNullOrEmpty(continuationToken)
                                                                                    ? Global.YTMusicParams
                                                                                    : Global.YTMusicParams.Replace('?', '&')));

                    request = AddStandardHeaders(request, cookieValue);
                    request = AddApiHeaders(request, cookieValue);

                    byte[] postBytes = GetPostBytes(
                                            SetDynamicContext(
                                                SafeFileStream.ReadAllText(
                                                        Path.Combine(Global.WorkingDirectory, @"AppData\get_playlists_context.json"))));

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
                            var jo = JObject.Parse(result);

                            // The playlists grid used to be hard-coded at
                            // sectionListRenderer.contents[1].itemSectionRenderer.contents[0].gridRenderer,
                            // but YT Music has since re-nested it (it now typically sits directly under
                            // sectionListRenderer.contents[0]) - so locate the grid wherever it lives.
                            BrowsePlaylistsResultsContext.Gridrenderer grid = null;
                            var gridRendererTokens = jo.Descendants().Where(t => t.Type == JTokenType.Property && ((JProperty)t).Name == "gridRenderer")
                                                                     .Select(p => ((JProperty)p).Value)
                                                                     .ToList();

                            foreach (var token in gridRendererTokens)
                            {
                                try
                                {
                                    var candidate = token.ToObject<BrowsePlaylistsResultsContext.Gridrenderer>();
                                    if (candidate != null && candidate.items != null && candidate.items.Length > 0)
                                    {
                                        grid = candidate;
                                        break;
                                    }
                                }
                                catch { }
                            }

                            BrowsePlaylistsResultsContext.Item2[] playListResults = null;
                            if (grid != null)
                            {
                                playListResults = grid.items;
                            }
                            else
                            {
                                // Legacy layout fallback
                                var playListsResultContext = JsonConvert.DeserializeObject<BrowsePlaylistsResultsContext>(result);
                                playListResults = playListsResultContext.contents
                                                                        .singleColumnBrowseResultsRenderer
                                                                        .tabs[0]
                                                                        .tabRenderer
                                                                        .content
                                                                        .sectionListRenderer
                                                                        .contents[1]
                                                                        .itemSectionRenderer
                                                                        .contents[0]
                                                                        .gridRenderer
                                                                        .items;
                            }

                            foreach (var item in playListResults)
                            {
                                // The first grid item is a 'New playlist' tile (which has no browse
                                // endpoint) and must be skipped, as must the automatic 'Your likes' playlist
                                if (item.musicTwoRowItemRenderer == null ||
                                    item.musicTwoRowItemRenderer.title == null ||
                                    item.musicTwoRowItemRenderer.title.runs == null ||
                                    item.musicTwoRowItemRenderer.navigationEndpoint == null ||
                                    item.musicTwoRowItemRenderer.navigationEndpoint.browseEndpoint == null)
                                {
                                    continue;
                                }

                                if (item.musicTwoRowItemRenderer.title.runs[0].text != "New playlist" &&
                                    item.musicTwoRowItemRenderer.title.runs[0].text != "Your likes")
                                {
                                    try
                                    {
                                        var pl = new OnlinePlaylist
                                        {
                                            Title = item.musicTwoRowItemRenderer.title.runs[0].text
                                        };

                                        try
                                        {
                                            pl.Subtitle = item.musicTwoRowItemRenderer.subtitle.runs[0].text +
                                                  item.musicTwoRowItemRenderer.subtitle.runs[1].text +
                                                  item.musicTwoRowItemRenderer.subtitle.runs[2].text;
                                        }
                                        catch
                                        {
                                            try
                                            {
                                                pl.Subtitle = item.musicTwoRowItemRenderer.subtitle.runs[0].text +
                                                     item.musicTwoRowItemRenderer.subtitle.runs[1].text;
                                            }
                                            catch
                                            {
                                                try
                                                {
                                                    pl.Subtitle = item.musicTwoRowItemRenderer.subtitle.runs[0].text;
                                                }
                                                catch { }
                                            }
                                        }

                                        pl.BrowseId = item.musicTwoRowItemRenderer.navigationEndpoint.browseEndpoint.browseId;

                                        try
                                        {
                                            pl.CoverArtUrl = item.musicTwoRowItemRenderer.thumbnailRenderer.musicThumbnailRenderer.thumbnail.thumbnails[0].url;
                                        }
                                        catch { }

                                        playListCol.Add(pl);

                                    }
                                    catch { }
                                }
                            }

                            string continuation = string.Empty;
                            if (grid != null &&
                                grid.continuations != null &&
                                grid.continuations.Length > 0 &&
                                grid.continuations[0].nextContinuationData != null &&
                                grid.continuations[0].nextContinuationData.continuation != null)
                            {
                                continuation = grid.continuations[0].nextContinuationData.continuation;
                            }
                            else if (grid == null)
                            {
                                // Legacy layout fallback
                                var musicShelfRendererTokens = jo.Descendants().Where(t => t.Type == JTokenType.Property && ((JProperty)t).Name == "itemSectionRenderer")
                                                                               .Select(p => ((JProperty)p).Value)
                                                                               .ToList();

                                foreach (var token in musicShelfRendererTokens)
                                {
                                    var msr = token.ToObject<BrowsePlaylistsResultsContext.Itemsectionrenderer>();
                                    if (msr != null &&
                                        msr.contents[0].gridRenderer.continuations != null &&
                                        msr.contents[0].gridRenderer.continuations.Length > 0 &&
                                        msr.contents[0].gridRenderer.continuations[0].nextContinuationData != null &&
                                        msr.contents[0].gridRenderer.continuations[0].nextContinuationData.continuation != null)
                                    {
                                        continuation = msr.contents[0].gridRenderer.continuations[0].nextContinuationData.continuation;
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(continuation))
                                return GetPlaylists(cookieValue, continuation, playListCol);
                        }
                        else
                        {
                            var playListsResultContext = JsonConvert.DeserializeObject<BrowsePlaylistsResultsContinuationContext>(result);
                            var playListResults = playListsResultContext.continuationContents
                                                                        .gridContinuation
                                                                        .items;

                            foreach (var item in playListResults)
                            {
                                if (item.musicTwoRowItemRenderer == null ||
                                    item.musicTwoRowItemRenderer.title == null ||
                                    item.musicTwoRowItemRenderer.title.runs == null ||
                                    item.musicTwoRowItemRenderer.navigationEndpoint == null ||
                                    item.musicTwoRowItemRenderer.navigationEndpoint.browseEndpoint == null)
                                {
                                    continue;
                                }

                                if (item.musicTwoRowItemRenderer.title.runs[0].text != "New playlist" &&
                                    item.musicTwoRowItemRenderer.title.runs[0].text != "Your likes")
                                {
                                    try
                                    {

                                        var pl = new OnlinePlaylist
                                        {
                                            Title = item.musicTwoRowItemRenderer.title.runs[0].text
                                        };

                                        try
                                        {
                                            pl.Subtitle = item.musicTwoRowItemRenderer.subtitle.runs[0].text +
                                                  item.musicTwoRowItemRenderer.subtitle.runs[1].text +
                                                  item.musicTwoRowItemRenderer.subtitle.runs[2].text;
                                        }
                                        catch
                                        {
                                            try
                                            {
                                                pl.Subtitle = item.musicTwoRowItemRenderer.subtitle.runs[0].text +
                                                     item.musicTwoRowItemRenderer.subtitle.runs[1].text;
                                            }
                                            catch
                                            {
                                                try
                                                {
                                                    pl.Subtitle = item.musicTwoRowItemRenderer.subtitle.runs[0].text;
                                                }
                                                catch { }
                                            }
                                        }

                                        pl.BrowseId = item.musicTwoRowItemRenderer.navigationEndpoint.browseEndpoint.browseId;

                                        try
                                        {
                                            pl.CoverArtUrl = item.musicTwoRowItemRenderer.thumbnailRenderer.musicThumbnailRenderer.thumbnail.thumbnails[0].url;
                                        }
                                        catch { }

                                        playListCol.Add(pl);
                                    }
                                    catch (Exception e)
                                    {
                                        Logger.Log(e, "GetPlaylists - Error fetching a playlist", Log.LogTypeEnum.Error);
                                    }
                                }
                            }

                            string continuation = string.Empty;
                            var jo = JObject.Parse(result);
                            var musicShelfRendererTokens = jo.Descendants().Where(t => t.Type == JTokenType.Property && ((JProperty)t).Name == "continuationContents")
                                                                           .Select(p => ((JProperty)p).Value)
                                                                           .ToList();

                            foreach (var token in musicShelfRendererTokens)
                            {
                                var msr = token.ToObject<BrowsePlaylistsResultsContinuationContext.Continuationcontents>();
                                if (msr != null &&
                                    msr.gridContinuation != null &&
                                    msr.gridContinuation.continuations != null &&
                                    msr.gridContinuation.continuations.Length > 0 &&
                                    msr.gridContinuation.continuations[0].nextContinuationData != null &&
                                    msr.gridContinuation.continuations[0].nextContinuationData.continuation != null)
                                {
                                    continuation = msr.gridContinuation.continuations[0].nextContinuationData.continuation;
                                }
                            }

                            if (!string.IsNullOrEmpty(continuation))
                                return GetPlaylists(cookieValue, continuation, playListCol);
                        }
                    }
                }
                catch (Exception e)
                {
                    var _ = e;
#if DEBUG
                    Console.Out.WriteLine("GetPlaylists: " + e.Message);
#endif
                }

                return playListCol;
            }
        }
    }
}
