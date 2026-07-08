using JBToolkit.StreamHelpers;
using Newtonsoft.Json;
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
        /// HttpWebRequest POST request to send to YouTube delete a YT music track fro the users uploads
        /// </summary>
        /// <param name="cookieValue">Cookie from a previous YouTube Music sign in via this application (stored in the database)</param>
        /// <param name="entityId">YT Music entity ID to delete</param>
        /// <returns>True if successfully authenticated, false otherwise</returns>
        public static bool DeleteAlbumOrTrackFromYTMusic(string cookieValue, string entityId, out string errorMessage)
        {
            errorMessage = string.Empty;

            if (Global.PreferPythonBridge && BridgeService.TryEnsureSession(cookieValue))
            {
                try
                {
                    BridgeService.Invoke("delete_upload_entity", new Newtonsoft.Json.Linq.JObject
                    {
                        ["entityId"] = entityId
                    });

                    return true;
                }
                catch (BridgeUnavailableException)
                {
                    // Bridge process died - fall through to the native implementation below
                }
                catch (BridgeException e)
                {
                    // Command failure - same as the native failure path
                    errorMessage = "Error: " + e.Message;
                    return false;
                }
            }

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(
                                                        Global.YTMusicBaseUrl +
                                                        "music/delete_privately_owned_entity" +
                                                        Global.YTMusicParams);

                request = AddStandardHeaders(request, cookieValue);
                request = AddApiHeaders(request, cookieValue);

                var context = JsonConvert.DeserializeObject<DeleteFromYTMusicRequestContext>(
                                              SafeFileStream.ReadAllText(
                                                                  Path.Combine(
                                                                          Global.WorkingDirectory,
                                                                          @"AppData\delete_song_context.json")));

                context.entityId = entityId;
                byte[] postBytes = GetPostBytes(SetDynamicContext(JsonConvert.SerializeObject(context)));
                request.ContentLength = postBytes.Length;

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
