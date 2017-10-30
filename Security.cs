#region Related components
using System;
using System.Web;
using System.Web.Security;
using System.Security.Cryptography;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Components.Utility
{
	/// <summary>
	/// Static servicing methods for working security in ASP.NET
	/// </summary>
	public static class AspNetSecurityService
	{
		/// <summary>
		/// Gets the authenticate token (means the encrypted authenticated ticket)
		/// </summary>
		/// <param name="userID"></param>
		/// <param name="accessToken"></param>
		/// <param name="sessonID"></param>
		/// <param name="deviceID"></param>
		/// <param name="expiration"></param>
		/// <param name="persistent"></param>
		/// <returns></returns>
		public static string GetAuthenticateToken(string userID, string accessToken = null, string sessonID = null, string deviceID = null, int expiration = 5, bool persistent = false)
		{
			var data = new JObject();
			if (!string.IsNullOrWhiteSpace(sessonID))
				data.Add(new JProperty("SessionID", sessonID));
			if (!string.IsNullOrWhiteSpace(sessonID))
				data.Add(new JProperty("DeviceID", deviceID));
			if (!string.IsNullOrWhiteSpace(accessToken))
				data.Add(new JProperty("AccessToken", accessToken));

			var ticket = new FormsAuthenticationTicket(1, userID, DateTime.Now, DateTime.Now.AddMinutes(expiration > 0 ? expiration : 5), persistent, data.ToString(Formatting.None));
			return FormsAuthentication.Encrypt(ticket);
		}

		/// <summary>
		/// Parses the authenticate token (return a tuple value with first element is user identity, second element is access token, third element is session identity, and last element is device identity)
		/// </summary>
		/// <param name="ticket"></param>
		/// <param name="rsaCrypto"></param>
		/// <param name="aesKey"></param>
		/// <returns></returns>
		public static Tuple<string, string, string, string> ParseAuthenticateToken(string ticket, RSACryptoServiceProvider rsaCrypto, string aesKey)
		{
			try
			{
				var authTicket = FormsAuthentication.Decrypt(ticket);
				var data = JObject.Parse(authTicket.UserData);
				var userID = authTicket.Name;
				var accessToken = data["AccessToken"] != null
					? (data["AccessToken"] as JValue).Value as string
					: null;
				var sessionID = data["SessionID"] != null
					? (data["SessionID"] as JValue).Value as string
					: null;
				var deviceID = data["DeviceID"] != null
					? (data["DeviceID"] as JValue).Value as string
					: null;
				return new Tuple<string, string, string, string>(userID, accessToken, sessionID, deviceID);
			}
			catch
			{
				return new Tuple<string, string, string, string>("", "", "", "");
			}
		}

		/// <summary>
		/// Gets the authenticate cookie (means the cookie with encrypted authenticated ticket)
		/// </summary>
		/// <param name="userID"></param>
		/// <param name="accessToken"></param>
		/// <param name="sessonID"></param>
		/// <param name="deviceID"></param>
		/// <param name="expiration"></param>
		/// <param name="persistent"></param>
		/// <returns></returns>
		public static HttpCookie GetAuthenticateCookie(string userID, string accessToken = null, string sessonID = null, string deviceID = null, int expiration = 5, bool persistent = false)
		{
			return new HttpCookie(FormsAuthentication.FormsCookieName)
			{
				Value = AspNetSecurityService.GetAuthenticateToken(userID, accessToken, sessonID, deviceID, expiration, persistent),
				HttpOnly = true
			};
		}

		/// <summary>
		/// Gets the authenticate cookie (means the cookie with encrypted authenticated ticket)
		/// </summary>
		/// <param name="userID"></param>
		/// <param name="persistent"></param>
		/// <returns></returns>
		public static HttpCookie GetAuthenticateCookie(string userID, bool persistent)
		{
			return AspNetSecurityService.GetAuthenticateCookie(userID, null, null, null, FormsAuthentication.Timeout.TotalMinutes.CastAs<int>(), persistent);
		}
	}
}