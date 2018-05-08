#region Related components
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.WebSockets;
using System.Web.Configuration;
using System.Configuration;
using System.Collections.Generic;
using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;

using Newtonsoft.Json.Linq;

using net.vieapps.Components.Security;
using net.vieapps.Components.WebSockets;
#endregion

namespace net.vieapps.Components.Utility
{
	/// <summary>
	/// Static servicing methods for working with ASP.NET
	/// </summary>
	public static partial class AspNetUtilityService
	{

		#region Extension helpers
		internal const long MinSmallFileSize = 1024 * 40;
		internal const long MaxSmallFileSize = 1024 * 1024 * 2;

		/// <summary>
		/// Gets max length of body request
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		public static long GetBodyRequestMaxLength(this HttpContext context)
		{
			return ConfigurationManager.GetSection("system.web/httpRuntime") is HttpRuntimeSection httpRuntime
				? httpRuntime.MaxRequestLength.CastAs<long>()
				: Int64.MaxValue;
		}

		/// <summary>
		/// Gets the request ETag
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		public static string GetRequestETag(this HttpContext context)
		{
			// IE or common browser
			var requestETag = context.Request.Headers["If-Range"];

			// FireFox
			if (string.IsNullOrWhiteSpace(requestETag))
				requestETag = context.Request.Headers["If-Match"];

			// normalize
			if (!string.IsNullOrWhiteSpace(requestETag))
			{
				while (requestETag.StartsWith("\""))
					requestETag = requestETag.Right(requestETag.Length - 1);
				while (requestETag.EndsWith("\""))
					requestETag = requestETag.Left(requestETag.Length - 1);
			}

			// return the request ETag for resume downloading
			return requestETag;
		}

		/// <summary>
		/// Sets the approriate headers of response
		/// </summary>
		/// <param name="context"></param>
		/// <param name="statusCode"></param>
		/// <param name="contentType"></param>
		/// <param name="eTag"></param>
		/// <param name="lastModified"></param>
		/// <param name="correlationID"></param>
		/// <param name="additionalHeaders"></param>
		public static void SetResponseHeaders(this HttpContext context, int statusCode, string contentType, string eTag = null, string lastModified = null, string correlationID = null, Dictionary<string, string> additionalHeaders = null)
		{
			// update status code
			context.Response.StatusCode = statusCode;

			// prepare headers
			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				{ "Server", "VIEApps NGX" },
				{ "Content-Type", $"{contentType}; charset=utf-8" }
			};

			if (!string.IsNullOrWhiteSpace(eTag))
				headers.Add("ETag", $"\"{eTag}\"");

			if (!string.IsNullOrWhiteSpace(lastModified))
				headers.Add("Last-Modified", lastModified);

			if (!string.IsNullOrWhiteSpace(correlationID))
				headers.Add("X-Correlation-ID", correlationID);

			additionalHeaders?.Where(kvp => !headers.ContainsKey(kvp.Key)).ForEach(kvp => headers[kvp.Key] = kvp.Value);

			if (context.Items.Contains("PipelineStopwatch") && context.Items["PipelineStopwatch"] is Stopwatch stopwatch)
			{
				stopwatch.Stop();
				headers.Add("X-Execution-Times", stopwatch.GetElapsedTimes());
			}

			// update headers
			headers.ForEach(kvp => context.Response.Headers[kvp.Key] = kvp.Value);
		}

		/// <summary>
		/// Gets the approriate HTTP Status Code of the exception
		/// </summary>
		/// <param name="exception"></param>
		/// <returns></returns>
		public static int GetHttpStatusCode(this Exception exception)
		{
			if (exception is FileNotFoundException || exception is ServiceNotFoundException || exception is InformationNotFoundException)
				return (int)HttpStatusCode.NotFound;

			if (exception is AccessDeniedException)
				return (int)HttpStatusCode.Forbidden;

			if (exception is UnauthorizedException)
				return (int)HttpStatusCode.Unauthorized;

			if (exception is MethodNotAllowedException)
				return (int)HttpStatusCode.MethodNotAllowed;

			if (exception is InvalidRequestException)
				return (int)HttpStatusCode.BadRequest;

			if (exception is NotImplementedException)
				return (int)HttpStatusCode.NotImplemented;

			if (exception is ConnectionTimeoutException)
				return (int)HttpStatusCode.RequestTimeout;

			return (int)HttpStatusCode.InternalServerError;
		}

		/// <summary>
		/// Gets the original Uniform Resource Identifier (URI) of the request that was sent by the client
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		public static Uri GetRequestUri(this HttpContext context)
		{
			return context.Request.Url;
		}

		/// <summary>
		/// Parses the query of an uri
		/// </summary>
		/// <param name="uri"></param>
		/// /// <param name="onCompleted">Action to run on parsing completed</param>
		/// <returns>The collection of key and value pair</returns>
		public static Dictionary<string, string> ParseQuery(this Uri uri, Action<Dictionary<string, string>> onCompleted = null)
		{
			var query = HttpUtility.ParseQueryString(uri.Query).ToDictionary();
			onCompleted?.Invoke(query);
			return query;
		}

		/// <summary>
		/// Gets the requesting information
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		public static Tuple<Uri, EndPoint, EndPoint> GetRequestInfo(this HttpContext context)
		{
			var uri = context.GetRequestUri();
			var serviceProvider = context as IServiceProvider;
			var httpWorker = serviceProvider?.GetService<HttpWorkerRequest>();
			var remoteAddress = httpWorker == null ? context.Request.UserHostAddress : httpWorker.GetRemoteAddress();
			var remotePort = httpWorker == null ? 0 : httpWorker.GetRemotePort();
			var remoteEndpoint = IPAddress.TryParse(remoteAddress, out IPAddress ipAddress)
				? new IPEndPoint(ipAddress, remotePort > 0 ? remotePort : uri.Port) as EndPoint
				: new DnsEndPoint(context.Request.UserHostName, remotePort > 0 ? remotePort : uri.Port) as EndPoint;
			var localAddress = httpWorker == null ? uri.Host : httpWorker.GetLocalAddress();
			var localPort = httpWorker == null ? 0 : httpWorker.GetLocalPort();
			var localEndpoint = IPAddress.TryParse(localAddress, out ipAddress)
				? new IPEndPoint(ipAddress, localPort > 0 ? localPort : uri.Port) as EndPoint
				: new DnsEndPoint(uri.Host, localPort > 0 ? localPort : uri.Port) as EndPoint;
			return new Tuple<Uri, EndPoint, EndPoint>(uri, remoteEndpoint, localEndpoint);
		}
		#endregion

		#region Flush & Clear response
		/// <summary>
		/// Sends all currently buffered output to the client
		/// </summary>
		/// <param name="context"></param>
		public static void Flush(this HttpContext context) => context.Response.OutputStream.Flush();

		/// <summary>
		/// Asynchronously sends all currently buffered output to the client
		/// </summary>
		/// <param name="context"></param>
		/// <param name="cancellationToken"></param>
		public static async Task FlushAsync(this HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Response.ClientDisconnectedToken))
			{
				await context.Response.OutputStream.FlushAsync(cts.Token).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Clears the response
		/// </summary>
		/// <param name="context"></param>
		public static void Clear(this HttpContext context)
		{
			context.Response.ClearHeaders();
			context.Response.ClearContent();
		}
		#endregion

		#region Read data from request
		/// <summary>
		/// Reads data from request body
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		public static byte[] Read(this HttpContext context)
		{
			var data = new byte[0];
			var buffer = new byte[TextFileReader.BufferSize];
			var read = context.Request.InputStream.Read(buffer, 0, buffer.Length);
			while (read > 0)
			{
				data = data.Concat(buffer.Take(0, read));
				read = context.Request.InputStream.Read(buffer, 0, buffer.Length);
			}
			return data;
		}

		/// <summary>
		/// Reads data from request body
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		public static async Task<byte[]> ReadAsync(this HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			var data = new byte[0];
			var buffer = new byte[TextFileReader.BufferSize];
			var read = await context.Request.InputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
			while (read > 0)
			{
				data = data.Concat(buffer.Take(0, read));
				read = await context.Request.InputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
			}
			return data;
		}

		/// <summary>
		/// Reads data as text from request body
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		public static string ReadText(this HttpContext context)
		{
			using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
			{
				return reader.ReadToEnd();
			}
		}

		/// <summary>
		/// Reads data as text from request body
		/// </summary>
		/// <param name="context"></param>
		/// <returns></returns>
		public static async Task<string> ReadTextAsync(this HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
			{
				return await reader.ReadToEndAsync().WithCancellationToken(cancellationToken).ConfigureAwait(false);
			}
		}
		#endregion

		#region Write binary data to the response body
		/// <summary>
		/// Writes binary data to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="buffer"></param>
		/// <param name="offset"></param>
		/// <param name="count"></param>
		public static void Write(this HttpContext context, byte[] buffer, int offset = 0, int count = 0)
		{
			context.Response.OutputStream.Write(buffer, offset > -1 ? offset : 0, count > 0 ? count : buffer.Length);
		}

		/// <summary>
		/// Writes binary data to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="buffer"></param>
		/// <returns></returns>
		public static void Write(this HttpContext context, ArraySegment<byte> buffer)
		{
			context.Response.OutputStream.Write(buffer.Array, buffer.Offset, buffer.Count);
		}

		/// <summary>
		/// Writes binary data to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="buffer"></param>
		/// <param name="offset"></param>
		/// <param name="count"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task WriteAsync(this HttpContext context, byte[] buffer, int offset = 0, int count = 0, CancellationToken cancellationToken = default(CancellationToken))
		{
			using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Response.ClientDisconnectedToken))
			{
				await context.Response.OutputStream.WriteAsync(buffer, offset > -1 ? offset : 0, count > 0 ? count : buffer.Length, cts.Token).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Writes binary data to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="buffer"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task WriteAsync(this HttpContext context, ArraySegment<byte> buffer, CancellationToken cancellationToken = default(CancellationToken))
		{
			return context.WriteAsync(buffer.Array, buffer.Offset, buffer.Count, cancellationToken);
		}

		/// <summary>
		/// Writes the stream directly to output stream
		/// </summary>
		/// <param name="context"></param>
		/// <param name="stream">The stream to write</param>
		/// <param name="contentType">The MIME type</param>
		/// <param name="eTag">The entity tag</param>
		/// <param name="lastModified">The last-modified time in HTTP date-time format</param>
		/// <param name="contentDisposition">The string that presents name of attachment file, let it empty/null for writting showing/displaying (not for downloading attachment file)</param>
		/// <param name="blockSize">Size of one block to write</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task WriteAsync(this HttpContext context, Stream stream, string contentType, string eTag = null, string lastModified = null, string contentDisposition = null, int blockSize = 0, CancellationToken cancellationToken = default(CancellationToken))
		{
			// validate whether the file is too large
			var totalBytes = stream.Length;
			if (totalBytes > context.GetBodyRequestMaxLength())
			{
				context.Response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
				context.Response.StatusDescription = "Request Entity Too Large";
				return;
			}

			// check ETag for supporting resumeable downloaders
			if (!string.IsNullOrWhiteSpace(eTag))
			{
				var requestETag = context.GetRequestETag();
				if (!string.IsNullOrWhiteSpace(requestETag) && !eTag.Equals(requestETag))
				{
					context.Response.StatusCode = (int)HttpStatusCode.PreconditionFailed;
					context.Response.StatusDescription = "Precondition Failed";
					return;
				}
			}

			// prepare position for flushing as partial blocks
			var flushAsPartialContent = false;
			long startBytes = 0, endBytes = totalBytes - 1;
			if (!string.IsNullOrWhiteSpace(context.Request.Headers["Range"]))
			{
				var requestedRange = context.Request.Headers["Range"];
				var range = requestedRange.Split(new char[] { '=', '-' });

				startBytes = range[1].CastAs<long>();
				if (startBytes >= totalBytes)
				{
					context.Response.StatusCode = (int)HttpStatusCode.PreconditionFailed;
					context.Response.StatusDescription = "Precondition Failed";
					return;
				}

				flushAsPartialContent = true;

				if (startBytes < 0)
					startBytes = 0;

				try
				{
					endBytes = range[2].CastAs<long>();
				}
				catch { }

				if (endBytes > totalBytes - 1)
					endBytes = totalBytes - 1;
			}

			// prepare headers
			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				{ "Content-Length", $"{(endBytes - startBytes) + 1}" },
			};

			if (flushAsPartialContent && startBytes > -1)
				headers.Add("Content-Range", $"bytes {startBytes}-{endBytes}/{totalBytes}");

			if (!string.IsNullOrWhiteSpace(eTag))
				headers.Add("Accept-Ranges", "bytes");

			if (!string.IsNullOrWhiteSpace(contentDisposition))
				headers.Add("Content-Disposition", $"Attachment; Filename=\"{contentDisposition}\"");

			// update headers
			try
			{
				context.SetResponseHeaders(flushAsPartialContent ? (int)HttpStatusCode.PartialContent : (int)HttpStatusCode.OK, contentType, eTag, lastModified, null, headers);
				if (flushAsPartialContent)
					context.Response.StatusDescription = "Partial Content";
				await context.FlushAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (HttpException ex)
			{
				if (ex.Message.Contains("0x800704CD") || ex.Message.Contains("0x800703E3") || ex.Message.Contains("The remote host closed the connection"))
					return;
				throw ex;
			}
			catch (Exception)
			{
				throw;
			}

			// write small file directly to output stream
			if (!flushAsPartialContent && totalBytes <= AspNetUtilityService.MaxSmallFileSize)
				try
				{
					var buffer = new byte[totalBytes];
					var readBytes = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
					await context.WriteAsync(buffer, 0, readBytes, cancellationToken).ConfigureAwait(false);
					await context.FlushAsync(cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (HttpException ex)
				{
					if (ex.Message.Contains("0x800704CD") || ex.Message.Contains("0x800703E3") || ex.Message.Contains("The remote host closed the connection"))
						return;
					throw ex;
				}
				catch (Exception)
				{
					throw;
				}

			// flush to output stream
			else
			{
				// prepare blocks for writing
				var packSize = blockSize > 0
					? blockSize
					: (int)AspNetUtilityService.MinSmallFileSize;
				if (packSize > (endBytes - startBytes))
					packSize = (int)(endBytes - startBytes) + 1;
				var totalBlocks = (int)Math.Ceiling((endBytes - startBytes + 0.0) / packSize);

				// jump to requested position
				stream.Seek(startBytes > 0 ? startBytes : 0, SeekOrigin.Begin);

				// read and flush stream data to response stream
				var buffer = new byte[packSize];
				var readBlocks = 0;
				while (readBlocks < totalBlocks)
					try
					{
						var readBytes = await stream.ReadAsync(buffer, 0, packSize, cancellationToken).ConfigureAwait(false);
						if (readBytes > 0)
						{
							await context.WriteAsync(buffer, 0, readBytes, cancellationToken).ConfigureAwait(false);
							await context.FlushAsync(cancellationToken).ConfigureAwait(false);
						}
						readBlocks++;
					}
					catch (OperationCanceledException)
					{
						return;
					}
					catch (HttpException ex)
					{
						if (ex.Message.Contains("0x800704CD") || ex.Message.Contains("0x800703E3") || ex.Message.Contains("The remote host closed the connection"))
							return;
						throw ex;
					}
					catch (Exception)
					{
						throw;
					}
			}
		}

		/// <summary>
		/// Writes the binary data directly to output stream
		/// </summary>
		/// <param name="context"></param>
		/// <param name="buffer">The data to write</param>
		/// <param name="contentType">The MIME type</param>
		/// <param name="eTag">The entity tag</param>
		/// <param name="lastModified">The last-modified time in HTTP date-time format</param>
		/// <param name="contentDisposition">The string that presents name of attachment file, let it empty/null for writting showing/displaying (not for downloading attachment file)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task WriteAsync(this HttpContext context, byte[] buffer, string contentType, string eTag = null, string lastModified = null, string contentDisposition = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			using (var stream = buffer.ToMemoryStream())
			{
				await context.WriteAsync(stream, contentType, eTag, lastModified, contentDisposition, 0, cancellationToken).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Writes the content of a file (binnary) to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="fileInfo">The information of the file</param>
		/// <param name="contentType">The MIME type</param>
		/// <param name="contentDisposition">The string that presents name of attachment file, let it empty/null for writting showing/displaying (not for downloading attachment file)</param>
		/// <param name="eTag">The entity tag</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task WriteAsync(this HttpContext context, FileInfo fileInfo, string contentType, string eTag = null, string contentDisposition = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (fileInfo == null || !fileInfo.Exists)
				throw new FileNotFoundException("Not found" + (fileInfo != null ? " [" + fileInfo.Name + "]" : ""));

			using (var stream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
			{
				await context.WriteAsync(stream, contentType, eTag, fileInfo.LastWriteTime.ToHttpString(), contentDisposition, 0, cancellationToken).ConfigureAwait(false);
			}
		}
		#endregion

		#region Write a data-set as Excel document to the response body
		/// <summary>
		/// Writes a data-set as Excel document to to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="dataSet"></param>
		/// <param name="filename"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task WriteAsExcelDocumentAsync(this HttpContext context, DataSet dataSet, string filename = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			using (var stream = await dataSet.SaveAsExcelAsync(cancellationToken).ConfigureAwait(false))
			{
				filename = filename ?? dataSet.Tables[0].TableName + ".xlsx";
				await context.WriteAsync(stream, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null, null, filename, TextFileReader.BufferSize, cancellationToken).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Writes a data-set as Excel document to HTTP output stream directly
		/// </summary>
		/// <param name="context"></param>
		/// <param name="dataSet"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task WriteAsExcelDocumentAsync(this HttpContext context, DataSet dataSet, CancellationToken cancellationToken = default(CancellationToken))
		{
			return context.WriteAsExcelDocumentAsync(dataSet, null, cancellationToken);
		}
		#endregion

		#region Write text data to the response body
		/// <summary>
		/// Writes the given text to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="text"></param>
		/// <param name="encoding"></param>
		public static void Write(this HttpContext context, string text, Encoding encoding = null)
		{
			context.Write(text.ToBytes(encoding));
		}

		/// <summary>
		/// Writes the given text to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="text"></param>
		/// <param name="encoding"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task WriteAsync(this HttpContext context, string text, Encoding encoding = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return context.WriteAsync(text.ToBytes(encoding).ToArraySegment(), cancellationToken);
		}

		/// <summary>
		/// Writes the given text to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="text"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task WriteAsync(this HttpContext context, string text, CancellationToken cancellationToken)
		{
			return context.WriteAsync(text.ToBytes().ToArraySegment(), cancellationToken);
		}

		/// <summary>
		/// Writes the given text to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="text"></param>
		/// <param name="contentType"></param>
		/// <param name="statusCode"></param>
		/// <param name="eTag"></param>
		/// <param name="lastModified"></param>
		/// <param name="correlationID"></param>
		public static void Write(this HttpContext context, string text, string contentType, int statusCode, string eTag, string lastModified, string correlationID = null)
		{
			context.SetResponseHeaders(statusCode, contentType, eTag, lastModified, correlationID);
			context.Write(text.ToBytes());
		}

		/// <summary>
		/// Writes the given text to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="text"></param>
		/// <param name="contentType"></param>
		/// <param name="statusCode"></param>
		/// <param name="correlationID"></param>
		public static void Write(this HttpContext context, string text, string contentType, int statusCode, string correlationID = null)
		{
			context.Write(text, contentType, statusCode, null, null, correlationID);
		}

		/// <summary>
		/// Writes the given text to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="text"></param>
		/// <param name="contentType"></param>
		/// <param name="statusCode"></param>
		/// <param name="eTag"></param>
		/// <param name="lastModified"></param>
		/// <param name="correlationID"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task WriteAsync(this HttpContext context, string text, string contentType, int statusCode, string eTag, string lastModified, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			context.SetResponseHeaders(statusCode, contentType, eTag, lastModified, correlationID);
			await context.WriteAsync(text.ToBytes().ToArraySegment(), cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Writes the given text to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="text"></param>
		/// <param name="contentType"></param>
		/// <param name="statusCode"></param>
		/// <param name="correlationID"></param>
		public static Task WriteAsync(this HttpContext context, string text, string contentType = "text/html", int statusCode = 200, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return context.WriteAsync(text, contentType, statusCode, null, null, correlationID, cancellationToken);
		}
		#endregion

		#region Write JSON data to the response body
		/// <summary>
		/// Writes the JSON to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="json"></param>
		/// <param name="formatting"></param>
		/// <param name="eTag"></param>
		/// <param name="lastModified"></param>
		/// <param name="correlationID"></param>
		public static void Write(this HttpContext context, JToken json, Newtonsoft.Json.Formatting formatting, string eTag, string lastModified, string correlationID = null)
		{
			context.Write(json?.ToString(formatting) ?? "{}", "application/json", (int)HttpStatusCode.OK, eTag, lastModified, correlationID);
		}

		/// <summary>
		/// Writes the JSON to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="json"></param>
		/// <param name="formatting"></param>
		/// <param name="correlationID"></param>
		public static void Write(this HttpContext context, JToken json, Newtonsoft.Json.Formatting formatting = Newtonsoft.Json.Formatting.None, string correlationID = null)
		{
			context.Write(json, formatting, null, null, correlationID);
		}

		/// <summary>
		/// Writes the JSON to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="json"></param>
		/// <param name="formatting"></param>
		/// <param name="eTag"></param>
		/// <param name="lastModified"></param>
		/// <param name="correlationID"></param>
		public static Task WriteAsync(this HttpContext context, JToken json, Newtonsoft.Json.Formatting formatting, string eTag, string lastModified, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return context.WriteAsync(json?.ToString(formatting) ?? "{}", "application/json", (int)HttpStatusCode.OK, eTag, lastModified, correlationID, cancellationToken);
		}

		/// <summary>
		/// Writes the JSON to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="json"></param>
		/// <param name="formatting"></param>
		/// <param name="correlationID"></param>
		public static Task WriteAsync(this HttpContext context, JToken json, Newtonsoft.Json.Formatting formatting = Newtonsoft.Json.Formatting.None, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return context.WriteAsync(json, formatting, null, null, correlationID, cancellationToken);
		}

		/// <summary>
		/// Writes the JSON to the response body
		/// </summary>
		/// <param name="context"></param>
		/// <param name="json"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task WriteAsync(this HttpContext context, JToken json, CancellationToken cancellationToken)
		{
			return context.WriteAsync(json, Newtonsoft.Json.Formatting.None, null, cancellationToken);
		}
		#endregion

		#region Show HTTP Error as HTML
		static string GetHttpErrorHtml(this HttpContext context, int statusCode, string message, string type, string correlationID = null, string stack = null, bool showStack = true)
		{
			var html = "<!DOCTYPE html>\r\n" +
				$"<html xmlns=\"http://www.w3.org/1999/xhtml\">\r\n" +
				$"<head><title>Error {statusCode}</title></head>\r\n<body>\r\n" +
				$"<h1>HTTP {statusCode} - {message.Replace("<", "&lt;").Replace(">", "&gt;")}</h1>\r\n" +
				$"<hr/>\r\n" +
				$"<div>Type: {type} {(!string.IsNullOrWhiteSpace(correlationID) ? " - Correlation ID: " + correlationID : "")}</div>\r\n";
			if (!string.IsNullOrWhiteSpace(stack) && showStack)
				html += $"<div><br/>Stack:</div>\r\n<blockquote>{stack.Replace("<", "&lt;").Replace(">", "&gt;").Replace("\n", "<br/>").Replace("\r", "").Replace("\t", "")}</blockquote>\r\n";
			html += "</body>\r\n</html>";
			return html;
		}

		/// <summary>
		/// Shows HTTP error as HTML
		/// </summary>
		/// <param name="context"></param>
		/// <param name="statusCode"></param>
		/// <param name="message"></param>
		/// <param name="type"></param>
		/// <param name="correlationID"></param>
		/// <param name="stack"></param>
		/// <param name="showStack"></param>
		public static void ShowHttpError(this HttpContext context, int statusCode, string message, string type, string correlationID = null, string stack = null, bool showStack = true)
		{
			statusCode = statusCode < 1 ? (int)HttpStatusCode.InternalServerError : statusCode;
			context.Clear();
			context.Write(context.GetHttpErrorHtml(statusCode, message, type, correlationID, stack, showStack), "text/html", statusCode, correlationID);
			if (message.IsContains("potentially dangerous"))
				context.Response.End();
		}

		/// <summary>
		/// Shows HTTP error as HTML
		/// </summary>
		/// <param name="context"></param>
		/// <param name="statusCode"></param>
		/// <param name="message"></param>
		/// <param name="type"></param>
		/// <param name="correlationID"></param>
		/// <param name="ex"></param>
		/// <param name="showStack"></param>
		public static void ShowHttpError(this HttpContext context, int statusCode, string message, string type, string correlationID, Exception ex, bool showStack = true)
		{
			var stack = string.Empty;
			if (ex != null && showStack)
			{
				stack = ex.StackTrace;
				var counter = 1;
				var inner = ex.InnerException;
				while (inner != null)
				{
					stack += "\r\n" + $" ---- Inner [{counter}] -------------------------------------- " + "\r\n" + inner.StackTrace;
					inner = inner.InnerException;
					counter++;
				}
			}
			context.ShowHttpError(statusCode, message, type, correlationID, stack, showStack);
		}

		/// <summary>
		/// Shows HTTP error as HTML
		/// </summary>
		/// <param name="context"></param>
		/// <param name="statusCode"></param>
		/// <param name="message"></param>
		/// <param name="type"></param>
		/// <param name="correlationID"></param>
		/// <param name="stack"></param>
		/// <param name="showStack"></param>
		/// <param name="cancellationToken"></param>
		public static async Task ShowHttpErrorAsync(this HttpContext context, int statusCode, string message, string type, string correlationID = null, string stack = null, bool showStack = true, CancellationToken cancellationToken = default(CancellationToken))
		{
			statusCode = statusCode < 1 ? (int)HttpStatusCode.InternalServerError : statusCode;
			context.Clear();
			await context.WriteAsync(context.GetHttpErrorHtml(statusCode, message, type, correlationID, stack, showStack), "text/html", statusCode, correlationID, cancellationToken).ConfigureAwait(false);
			if (message.IsContains("potentially dangerous"))
				context.Response.End();
		}

		/// <summary>
		/// Shows HTTP error as HTML
		/// </summary>
		/// <param name="context"></param>
		/// <param name="statusCode"></param>
		/// <param name="message"></param>
		/// <param name="type"></param>
		/// <param name="correlationID"></param>
		/// <param name="ex"></param>
		/// <param name="showStack"></param>
		/// <param name="cancellationToken"></param>
		public static Task ShowHttpErrorAsync(this HttpContext context, int statusCode, string message, string type, string correlationID, Exception ex, bool showStack = true, CancellationToken cancellationToken = default(CancellationToken))
		{
			var stack = string.Empty;
			if (ex != null && showStack)
			{
				stack = ex.StackTrace;
				var counter = 1;
				var inner = ex.InnerException;
				while (inner != null)
				{
					stack += "\r\n" + $" ---- Inner [{counter}] -------------------------------------- " + "\r\n" + inner.StackTrace;
					inner = inner.InnerException;
					counter++;
				}
			}
			return context.ShowHttpErrorAsync(statusCode, message, type, correlationID, stack, showStack, cancellationToken);
		}
		#endregion

		#region Write HTTP Error as JSON
		static JObject GetHttpErrorJson(this HttpContext context, int statusCode, string message, string type, string correlationID = null, JObject stack = null, bool showStack = true)
		{
			var json = new JObject()
			{
				{ "Message", message },
				{ "Type", type },
				{ "Code", statusCode }
			};

			if (!string.IsNullOrWhiteSpace(correlationID))
				json["CorrelationID"] = correlationID;

			if (stack != null && showStack)
				json["Stack"] = stack;

			return json;
		}

		/// <summary>
		/// Writes HTTP error as JSON
		/// </summary>
		/// <param name="context"></param>
		/// <param name="statusCode"></param>
		/// <param name="message"></param>
		/// <param name="type"></param>
		/// <param name="correlationID"></param>
		/// <param name="stack"></param>
		/// <param name="showStack"></param>
		public static void WriteHttpError(this HttpContext context, int statusCode, string message, string type, string correlationID = null, JObject stack = null, bool showStack = true)
		{
			statusCode = statusCode < 1 ? (int)HttpStatusCode.InternalServerError : statusCode;
			context.Clear();
			context.Write(context.GetHttpErrorJson(statusCode, message, type, correlationID, stack, showStack).ToString(Newtonsoft.Json.Formatting.Indented), "application/json", statusCode, correlationID);
			if (message.IsContains("potentially dangerous"))
				context.Response.End();
		}

		/// <summary>
		/// Writes HTTP error as JSON
		/// </summary>
		/// <param name="context"></param>
		/// <param name="statusCode"></param>
		/// <param name="message"></param>
		/// <param name="type"></param>
		/// <param name="correlationID"></param>
		/// <param name="ex"></param>
		/// <param name="showStack"></param>
		public static void WriteHttpError(this HttpContext context, int statusCode, string message, string type, string correlationID, Exception ex, bool showStack = true)
		{
			JObject stack = null;
			if (ex != null && showStack)
			{
				stack = new JObject()
				{
					{ "Stack", ex.StackTrace }
				};
				var inners = new JArray();
				var counter = 1;
				var inner = ex.InnerException;
				while (inner != null)
				{
					inners.Add(new JObject()
					{
						{ $"Inner_{counter}", inner.StackTrace }
					});
					inner = inner.InnerException;
					counter++;
				}
				if (inners.Count > 0)
					stack["Inners"] = inners;
			}
			context.WriteHttpError(statusCode, message, type, correlationID, stack, showStack);
		}

		/// <summary>
		/// Writes HTTP error as JSON
		/// </summary>
		/// <param name="context"></param>
		/// <param name="statusCode"></param>
		/// <param name="message"></param>
		/// <param name="type"></param>
		/// <param name="correlationID"></param>
		/// <param name="stack"></param>
		/// <param name="showStack"></param>
		/// <param name="cancellationToken"></param>
		public static async Task WriteHttpErrorAsync(this HttpContext context, int statusCode, string message, string type, string correlationID = null, JObject stack = null, bool showStack = true, CancellationToken cancellationToken = default(CancellationToken))
		{
			statusCode = statusCode < 1 ? (int)HttpStatusCode.InternalServerError : statusCode;
			context.Clear();
			await context.WriteAsync(context.GetHttpErrorJson(statusCode, message, type, correlationID, stack, showStack).ToString(Newtonsoft.Json.Formatting.Indented), "application/json", statusCode, correlationID, cancellationToken).ConfigureAwait(false);
			if (message.IsContains("potentially dangerous"))
				context.Response.End();
		}

		/// <summary>
		/// Writes HTTP error as JSON
		/// </summary>
		/// <param name="context"></param>
		/// <param name="statusCode"></param>
		/// <param name="message"></param>
		/// <param name="type"></param>
		/// <param name="correlationID"></param>
		/// <param name="ex"></param>
		/// <param name="showStack"></param>
		/// <param name="cancellationToken"></param>
		public static Task WriteHttpErrorAsync(this HttpContext context, int statusCode, string message, string type, string correlationID, Exception ex, bool showStack = true, CancellationToken cancellationToken = default(CancellationToken))
		{
			JObject stack = null;
			if (ex != null && showStack)
			{
				stack = new JObject()
				{
					{ "Stack", ex.StackTrace }
				};
				var inners = new JArray();
				var counter = 1;
				var inner = ex.InnerException;
				while (inner != null)
				{
					inners.Add(new JObject()
					{
						{ $"Inner_{counter}", inner.StackTrace }
					});
					inner = inner.InnerException;
					counter++;
				}
				if (inners.Count > 0)
					stack["Inners"] = inners;
			}
			return context.WriteHttpErrorAsync(statusCode, message, type, correlationID, stack, showStack, cancellationToken);
		}
		#endregion

		#region Wrap a WebSocket connection of ASP.NET into WebSocket component
		/// <summary>
		/// Wraps a WebSocket connection of ASP.NET
		/// </summary>
		/// <param name="websocket"></param>
		/// <param name="httpContext"></param>
		/// <param name="whenIsNotWebSocketRequest"></param>
		/// <returns></returns>
		public static void Wrap(this WebSocket websocket, HttpContext httpContext, Action<HttpContext> whenIsNotWebSocketRequest = null)
		{
			if (httpContext.IsWebSocketRequest)
				httpContext.AcceptWebSocketRequest(async (websocketContext) =>
				{
					var info = httpContext.GetRequestInfo();
					await websocket.WrapAsync(websocketContext.WebSocket, info.Item1, info.Item2, info.Item3).ConfigureAwait(false);
				});
			else
				whenIsNotWebSocketRequest?.Invoke(httpContext);
		}

		/// <summary>
		/// Wraps a WebSocket connection of ASP.NET
		/// </summary>
		/// <param name="websocket"></param>
		/// <param name="httpContext"></param>
		/// <param name="whenIsNotWebSocketRequest"></param>
		/// <returns></returns>
		public static void WrapWebSocket(this WebSocket websocket, HttpContext httpContext, Action<HttpContext> whenIsNotWebSocketRequest = null)
		{
			websocket.Wrap(httpContext, whenIsNotWebSocketRequest);
		}

		/// <summary>
		/// Wraps a WebSocket connection of ASP.NET
		/// </summary>
		/// <param name="websocket"></param>
		/// <param name="websocketContext"></param>
		/// <returns></returns>
		public static Task WrapAsync(this WebSocket websocket, AspNetWebSocketContext websocketContext)
		{
			var httpContext = HttpContext.Current;
			var info = httpContext != null
				? httpContext.GetRequestInfo()
				: new Tuple<Uri, EndPoint, EndPoint>(websocketContext.RequestUri, new DnsEndPoint(websocketContext.UserHostName, websocketContext.RequestUri.Port), null);
			return websocket.WrapAsync(websocketContext.WebSocket, info.Item1, info.Item2, info.Item3);
		}

		/// <summary>
		/// Wraps a WebSocket connection of ASP.NET
		/// </summary>
		/// <param name="websocket"></param>
		/// <param name="websocketContext"></param>
		/// <returns></returns>
		public static Task WrapWebSocketAsync(this WebSocket websocket, AspNetWebSocketContext websocketContext)
		{
			return websocket.WrapAsync(websocketContext);
		}
		#endregion

	}
}