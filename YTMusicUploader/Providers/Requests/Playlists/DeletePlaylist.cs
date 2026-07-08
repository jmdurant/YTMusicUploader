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
            /// HttpWebRequest POST request to send to delete a playlist
            /// </summary>
            /// <param name="cookieValue">Cookie from a previous YouTube Music sign in via this application (stored in the database)</param>
            /// <param name="playlistId">Playlist id (or browse id) to delete</param>
            /// <param name="errorMessage">(Output) error message from error encountered during the request</param>
            /// <returns>True if request is successful, false otherwise</returns>
            public static bool DeletePlaylist(string cookieValue, string playlistId, out string errorMessage)
            {
                errorMessage = string.Empty;

                // The 'playlist/delete' endpoint requires the playlist id without the 'VL' prefix
                if (playlistId.StartsWith("VL"))
                    playlistId = playlistId.Substring(2, playlistId.Length - 2);

                // Preferred path: the Python bridge (ytmusicapi)
                if (Global.PreferPythonBridge && BridgeService.TryEnsureSession(cookieValue))
                {
                    try
                    {
                        BridgeService.Invoke("delete_playlist", new JObject
                        {
                            ["playlistId"] = playlistId
                        });

                        return true;
                    }
                    catch (BridgeUnavailableException)
                    {
                        // Bridge process died - fall through to the native HttpWebRequest implementation
                    }
                    catch (BridgeException e)
                    {
                        errorMessage = "Error: " + e.Message;
                        return false;
                    }
                }

                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(
                                                            Global.YTMusicBaseUrl +
                                                            "playlist/delete" +
                                                            Global.YTMusicParams);

                    request = AddStandardHeaders(request, cookieValue);
                    request = AddApiHeaders(request, cookieValue);

                    var context = JsonConvert.DeserializeObject<DeletePlaylistRequestContext>(
                                                  SafeFileStream.ReadAllText(
                                                                      Path.Combine(
                                                                              Global.WorkingDirectory,
                                                                              @"AppData\delete_playlist_context.json")));

                    context.playlistId = playlistId;

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

                    postBytes = null;
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        string result = ReadResponseBody(response);

                        if (result.ToLower().Contains("error"))
                        {
                            errorMessage = "Error: " + result;
                            return false;
                        }
                    }
                }
                catch (Exception e)
                {
                    errorMessage = "Error: " + e.Message;
                    return false;
                }

                return true;
            }
        }
    }
}
