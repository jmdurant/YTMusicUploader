using JBToolkit.StreamHelpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using YTMusicUploader.Providers.RequestModels;
using static YTMusicUploader.Providers.RequestModels.ArtistCache.OnlinePlaylist;

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
            /// HttpWebRequest POST request to send to YTM to create a new playlist
            /// </summary>
            /// <param name="cookieValue">Cookie from a previous YouTube Music sign in via this application (stored in the database)</param>
            /// <param name="title">Title to call the playlist</param>
            /// <param name="description">Description of the playlist (does not allow HTML tags)</param>
            /// <param name="videoIds">List of video ids (track ids) to create the playlist with</param>
            /// <param name="privacyStatus">PRIVATE, PUBLIC or UNLISTED - Default it PUBLIC</param>
            /// <param name="playlistId">(Output) playlist id once created</param>
            /// <param name="browseId">(Output) browse id once create (It's just the playlist ID prefixed with 'VL')</param>
            /// <param name="errorMessage">(Output)Any error message encountered during the request</param>
            /// <returns>True if request is successful, false otherwise</returns>
            public static bool CreatePlaylist(
                string cookieValue,
                string title,
                string description,
                List<string> videoIds,
                PrivacyStatusEmum privacyStatus,
                out string playlistId,
                out string browseId,
                out Exception ex)
            {
                ex = null;
                string originalRequest = string.Empty;
                browseId = string.Empty;
                playlistId = string.Empty;

                if (description == null)
                    description = string.Empty;

                // YouTube Music rejects (or crashes on) titles containing angled brackets
                if (title != null)
                    title = title.Replace("<", "").Replace(">", "");

                // Preferred path: the Python bridge (ytmusicapi). Its create command doesn't take
                // the initial track list, so the playlist is created first and the tracks are then
                // added with a follow-up command (rolled back on failure to keep the all-or-nothing
                // semantics of the native implementation)
                if (Global.PreferPythonBridge && BridgeService.TryEnsureSession(cookieValue))
                {
                    bool createdViaBridge = false;

                    try
                    {
                        var result = BridgeService.Invoke("create_playlist", new JObject
                        {
                            ["title"] = title,
                            ["description"] = description,
                            ["privacyStatus"] = privacyStatus.ToString().ToUpper()
                        });

                        playlistId = (string)result["playlistId"] ?? string.Empty;
                        createdViaBridge = true;

                        // The browse id is just the playlist id prefixed with 'VL' (which is what
                        // callers store and later hand back to GetPlaylist / GetPlaylists matching)
                        browseId = string.IsNullOrEmpty(playlistId) || playlistId.StartsWith("VL")
                                        ? playlistId
                                        : "VL" + playlistId;

                        if (videoIds != null && videoIds.Count > 0)
                        {
                            BridgeService.Invoke("add_playlist_items", new JObject
                            {
                                ["playlistId"] = playlistId,
                                ["videoIds"] = JArray.FromObject(videoIds)
                            });
                        }

                        return true;
                    }
                    catch (BridgeUnavailableException)
                    {
                        if (createdViaBridge && !string.IsNullOrEmpty(playlistId))
                        {
                            // The playlist was already created but the bridge died before all tracks
                            // were added. Falling through to the native path would create a duplicate
                            // playlist, so return the created one as-is - the missing tracks are
                            // reconciled on the next playlist-processing pass (which re-fetches the
                            // online playlist and adds only the tracks it's missing)
                            return true;
                        }

                        // Bridge died before creating anything - safe to fall through to native
                        playlistId = string.Empty;
                        browseId = string.Empty;
                    }
                    catch (BridgeException e)
                    {
                        // Best-effort roll back of a playlist that was created but couldn't be
                        // populated, then fail exactly like the native implementation would
                        if (createdViaBridge && !string.IsNullOrEmpty(playlistId))
                        {
                            try
                            {
                                BridgeService.Invoke("delete_playlist", new JObject
                                {
                                    ["playlistId"] = playlistId
                                });
                            }
                            catch { }
                        }

                        playlistId = string.Empty;
                        browseId = string.Empty;

                        Logger.LogError("CreatePlaylist", "Error creating playlist: " + title, e.Message);
                        ex = e;
                        return false;
                    }
                }

                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(
                                                            Global.YTMusicBaseUrl +
                                                            "playlist/create" +
                                                            Global.YTMusicParams);

                    request = AddStandardHeaders(request, cookieValue);
                    request = AddApiHeaders(request, cookieValue);

                    var context = JsonConvert.DeserializeObject<CreatePlaylistRequestContext>(
                                                  SafeFileStream.ReadAllText(
                                                                      Path.Combine(
                                                                              Global.WorkingDirectory,
                                                                              @"AppData\create_playlist_context.json")));

                    context.title = title;
                    context.description = description;
                    context.privacyStatus = privacyStatus.ToString().ToUpper();
                    context.videoIds = videoIds.ToArray();

                    string body = SetDynamicContext(JsonConvert.SerializeObject(
                                                        context,
                                                        Formatting.None,
                                                        new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                    originalRequest = body;

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

                        if (result.ToLower().Contains("error"))
                            throw new Exception("Error: " + result + ": Original Http Request: " + originalRequest);
                        else
                        {
                            var runObject = JObject.Parse(result);
                            var browseIds = runObject.Descendants()
                                                .Where(t => t.Type == JTokenType.Property && ((JProperty)t).Name == "browseId")
                                                .Select(p => ((JProperty)p).Value).ToList();

                            if (browseIds.Count > 0)
                                browseId = browseIds[0].ToString();

                            var playlistIds = runObject.Descendants()
                                                .Where(t => t.Type == JTokenType.Property && ((JProperty)t).Name == "playlistId")
                                                .Select(p => ((JProperty)p).Value).ToList();

                            if (playlistIds.Count > 0)
                                playlistId = playlistIds[0].ToString();
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError("CreatePlaylist", "Error creating playlist: " + title, originalRequest);
                    ex = e;
                    return false;
                }

                return true;
            }
        }
    }
}