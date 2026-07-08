using JBToolkit.StreamHelpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net;
using YTMusicUploader.Providers.RequestModels;

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
            /// HttpWebRequest POST request to send to add a playlist item (track) to an existing YouTube Music playlist
            /// </summary>
            /// <param name="cookieValue">Cookie from a previous YouTube Music sign in via this application (stored in the database)</param>
            /// <param name="playlistId">YT Music playlistid (or browseId) to add to</param>
            /// <param name="videoId">This is the unique track id to add</param>
            /// <returns>True if successfully, false otherwise</returns>
            public static bool AddPlaylistItem(string cookieValue, string playlistId, string videoId, out Exception ex)
            {
                ex = null;

                // Preferred path: the Python bridge (ytmusicapi)
                if (Global.PreferPythonBridge && BridgeService.TryEnsureSession(cookieValue))
                {
                    try
                    {
                        // The bridge (ytmusicapi) expects the playlist id without the 'VL' browse prefix
                        string bridgePlaylistId = playlistId.StartsWith("VL")
                                                        ? playlistId.Substring(2, playlistId.Length - 2)
                                                        : playlistId;

                        BridgeService.Invoke("add_playlist_items", new JObject
                        {
                            ["playlistId"] = bridgePlaylistId,
                            ["videoIds"] = new JArray(videoId)
                        });

                        return true;
                    }
                    catch (BridgeUnavailableException)
                    {
                        // Bridge process died - fall through to the native HttpWebRequest implementation
                    }
                    catch (BridgeException e)
                    {
                        ex = e;
                        return false;
                    }
                }

                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(
                                                            Global.YTMusicBaseUrl +
                                                            "browse/edit_playlist" +
                                                            Global.YTMusicParams);

                    request = AddStandardHeaders(request, cookieValue);
                    request = AddApiHeaders(request, cookieValue);

                    var context = JsonConvert.DeserializeObject<AddPlaylistItemContext>(
                                                  SafeFileStream.ReadAllText(
                                                                      Path.Combine(
                                                                              Global.WorkingDirectory,
                                                                              @"AppData\add_playlist_item_context.json")));

                    // The 'browse/edit_playlist' endpoint requires the playlist id without the 'VL' prefix
                    if (playlistId.StartsWith("VL"))
                        playlistId = playlistId.Substring(2, playlistId.Length - 2);

                    context.playlistId = playlistId;
                    context.actions[0].addedVideoId = videoId;

                    byte[] postBytes = GetPostBytes(
                                            SetDynamicContext(JsonConvert.SerializeObject(
                                                context,
                                                Formatting.None,
                                                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore })));

                    request.ContentLength = postBytes.Length;
                    using (var requestStream = request.GetRequestStream())
                    {
                        requestStream.Write(postBytes, 0, postBytes.Length);
                        requestStream.Close();
                    }

                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        string result = ReadResponseBody(response);

                        if (result.ToLower().Contains("error"))
                            throw new Exception("Error: " + result);
                    }
                }
                catch (Exception e)
                {
                    ex = e;
                    return false;
                }

                return true;
            }
        }
    }
}
