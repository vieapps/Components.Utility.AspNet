#region Related components
using System;
using System.Configuration;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Configuration;

using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Components.Utility
{
	/// <summary>
	/// Static servicing methods for working with ASP.NET
	/// </summary>
	public static partial class AspNetUtilityService
	{

		#region Write file to HTTP output stream directly
		/// <summary>
		/// Gets max request length (defined in 'system.web/httpRuntime' section of web.config file)
		/// </summary>
		public static int MaxRequestLength
		{
			get
			{
				var maxRequestLength = 30;
				if (ConfigurationManager.GetSection("system.web/httpRuntime") is HttpRuntimeSection httpRuntime)
					maxRequestLength = httpRuntime.MaxRequestLength / 1024;
				return maxRequestLength;
			}
		}

		internal static long MinSmallFileSize = 1024 * 40;                             // 40 KB
		internal static long MaxSmallFileSize = 1024 * 1024 * 2;                // 02 MB
		internal static long MaxAllowedSize = AspNetUtilityService.MaxRequestLength * 1024 * 1024;

		static string GetRequestETag(HttpContext context)
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
		/// Writes the content of the file directly to output stream
		/// </summary>
		/// <param name="context"></param>
		/// <param name="filePath">The path to file</param>
		/// <param name="contentType">The MIME type</param>
		/// <param name="contentDisposition">The string that presents name of attachment file, let it empty/null for writting showing/displaying (not for downloading attachment file)</param>
		/// <param name="eTag">The entity tag</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task WriteFileToOutputAsync(this HttpContext context, string filePath, string contentType, string eTag = null, string contentDisposition = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			await context.WriteFileToOutputAsync(new FileInfo(filePath), contentType, eTag, contentDisposition, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Writes the content of the file directly to output stream
		/// </summary>
		/// <param name="context"></param>
		/// <param name="fileInfo">The information of the file</param>
		/// <param name="contentType">The MIME type</param>
		/// <param name="contentDisposition">The string that presents name of attachment file, let it empty/null for writting showing/displaying (not for downloading attachment file)</param>
		/// <param name="eTag">The entity tag</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task WriteFileToOutputAsync(this HttpContext context, FileInfo fileInfo, string contentType, string eTag = null, string contentDisposition = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (fileInfo == null || !fileInfo.Exists)
				throw new FileNotFoundException("Not found" + (fileInfo != null ? " [" + fileInfo.Name + "]" : ""));

			using (var stream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
			{
				await context.WriteStreamToOutputAsync(stream, contentType, eTag, fileInfo.LastWriteTime.ToHttpString(), contentDisposition, 0, cancellationToken).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Writes the binary data directly to output stream
		/// </summary>
		/// <param name="context"></param>
		/// <param name="data">The data to write</param>
		/// <param name="contentType">The MIME type</param>
		/// <param name="eTag">The entity tag</param>
		/// <param name="lastModified">The last-modified time in HTTP date-time format</param>
		/// <param name="contentDisposition">The string that presents name of attachment file, let it empty/null for writting showing/displaying (not for downloading attachment file)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task WriteDataToOutputAsync(this HttpContext context, byte[] data, string contentType, string eTag = null, string lastModified = null, string contentDisposition = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			using (var stream = new MemoryStream(data))
			{
				await context.WriteStreamToOutputAsync(stream, contentType, eTag, lastModified, contentDisposition, 0, cancellationToken).ConfigureAwait(false);
			}
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
		public static async Task WriteStreamToOutputAsync(this HttpContext context, Stream stream, string contentType, string eTag = null, string lastModified = null, string contentDisposition = null, int blockSize = 0, CancellationToken cancellationToken = default(CancellationToken))
		{
			// validate whether the file is too large
			var totalBytes = stream.Length;
			if (totalBytes > AspNetUtilityService.MaxAllowedSize)
			{
				context.Response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
				context.Response.StatusDescription = "Request Entity Too Large";
				return;
			}

			// check ETag for supporting resumeable downloaders
			if (!string.IsNullOrWhiteSpace(eTag))
			{
				var requestETag = AspNetUtilityService.GetRequestETag(context);
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
			if (context.Request.Headers["Range"] != null)
			{
				var requestedRange = context.Request.Headers["Range"];
				var range = requestedRange.Split(new char[] { '=', '-' });

				startBytes = Convert.ToInt64(range[1]);
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
					endBytes = Convert.ToInt64(range[2]);
				}
				catch { }
				if (endBytes > totalBytes - 1)
					endBytes = totalBytes - 1;
			}

			// prepare headers
			var headers = new List<string[]>();

			if (!string.IsNullOrWhiteSpace(lastModified))
				headers.Add(new string[] { "Last-Modified", lastModified });

			if (!string.IsNullOrWhiteSpace(eTag))
			{
				headers.Add(new string[] { "Accept-Ranges", "bytes" });
				headers.Add(new string[] { "ETag", "\"" + eTag + "\"" });
			}

			if (flushAsPartialContent && startBytes > -1)
				headers.Add(new string[] { "Content-Range", string.Format(" bytes {0}-{1}/{2}", startBytes, endBytes, totalBytes) });

			headers.Add(new string[] { "Content-Length", ((endBytes - startBytes) + 1).ToString() });

			if (!string.IsNullOrWhiteSpace(contentDisposition))
				headers.Add(new string[] { "Content-Disposition", "Attachment; Filename=\"" + contentDisposition + "\"" });

			// flush headers to HttpResponse output stream
			try
			{
				context.Response.Clear();
				context.Response.Buffer = false;
				context.Response.ContentEncoding = Encoding.UTF8;
				context.Response.ContentType = contentType;

				// status code of partial content
				if (flushAsPartialContent)
				{
					context.Response.StatusCode = (int)HttpStatusCode.PartialContent;
					context.Response.StatusDescription = "Partial Content";
				}

				headers.ForEach(header => context.Response.Headers.Add(header[0], header[1]));

				await context.Response.FlushAsync().ConfigureAwait(false);
			}
			catch (HttpException ex)
			{
				var isDisconnected = ex.Message.Contains("0x800704CD") || ex.Message.Contains("0x800703E3") || ex.Message.Contains("The remote host closed the connection");
				if (!isDisconnected)
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
					var isDisconnected = false;
					var data = new byte[totalBytes];
					var readBytes = await stream.ReadAsync(data, 0, (int)totalBytes, cancellationToken).ConfigureAwait(false);
					try
					{
						await context.Response.OutputStream.WriteAsync(data, 0, readBytes, cancellationToken).ConfigureAwait(false);
					}
					catch (OperationCanceledException)
					{
						isDisconnected = true;
					}
					catch (HttpException ex)
					{
						isDisconnected = ex.Message.Contains("0x800704CD") || ex.Message.Contains("0x800703E3") || ex.Message.Contains("The remote host closed the connection");
						if (!isDisconnected)
							throw ex;
					}
					catch (Exception ex)
					{
						throw ex;
					}

					// flush the written buffer to client and update cache
					if (!isDisconnected)
						try
						{
							await context.Response.FlushAsync().ConfigureAwait(false);
						}
						catch (Exception)
						{
							throw;
						}
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
				var isDisconnected = false;
				var readBlocks = 0;
				while (readBlocks < totalBlocks)
				{
					// the client is still connected
					if (context.Response.IsClientConnected)
						try
						{
							var buffer = new byte[packSize];
							var readBytes = await stream.ReadAsync(buffer, 0, packSize, cancellationToken).ConfigureAwait(false);
							if (readBytes > 0)
							{
								// write data to output stream
								try
								{
									await context.Response.OutputStream.WriteAsync(buffer, 0, readBytes, cancellationToken).ConfigureAwait(false);
								}
								catch (OperationCanceledException)
								{
									isDisconnected = true;
									break;
								}
								catch (HttpException ex)
								{
									isDisconnected = ex.Message.Contains("0x800704CD") || ex.Message.Contains("0x800703E3") || ex.Message.Contains("The remote host closed the connection");
									if (!isDisconnected)
										throw ex;
									else
										break;
								}
								catch (Exception)
								{
									throw;
								}

								// flush the written buffer to client
								if (!isDisconnected)
									try
									{
										await context.Response.FlushAsync().ConfigureAwait(false);
									}
									catch (Exception ex)
									{
										throw ex;
									}
							}
							readBlocks++;
						}
						catch (Exception ex)
						{
							throw ex;
						}

					// the client is disconnected
					else
					{
						isDisconnected = true;
						break;
					}
				}
			}
		}
		#endregion

		#region Write a data-set as Excel document to HTTP output stream directly
		/// <summary>
		/// Writes a data-set as Excel document to HTTP output stream directly
		/// </summary>
		/// <param name="context"></param>
		/// <param name="dataSet"></param>
		/// <param name="filename"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task WriteAsExcelDocumentAsync(this HttpContext context, DataSet dataSet, string filename = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			using (var stream = await dataSet.CreateExcelStreamAsync(cancellationToken).ConfigureAwait(false))
			{
				filename = filename ?? dataSet.Tables[0].TableName + ".xlsx";
				await context.WriteStreamToOutputAsync(stream, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null, null, filename, TextFileReader.BufferSize, cancellationToken).ConfigureAwait(false);
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

		#region Http errors
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
		/// Show HTTP error
		/// </summary>
		/// <param name="context"></param>
		/// <param name="code"></param>
		/// <param name="message"></param>
		/// <param name="type"></param>
		/// <param name="correlationID"></param>
		/// <param name="stack"></param>
		/// <param name="showStack"></param>
		public static void ShowHttpError(this HttpContext context, int code, string message, string type, string correlationID = null, string stack = null, bool showStack = true)
		{
			code = code < 1 ? (int)HttpStatusCode.InternalServerError : code;

			context.Response.TrySkipIisCustomErrors = true;
			context.Response.StatusCode = code;
			context.Response.Cache.SetNoStore();
			context.Response.ContentType = "text/html";

			context.Response.ClearContent();
			context.Response.Output.Write("<!DOCTYPE html>\r\n");
			context.Response.Output.Write("<html xmlns=\"http://www.w3.org/1999/xhtml\">\r\n");
			context.Response.Output.Write("<head><title>Error " + code.ToString() + "</title></head>\r\n<body>\r\n");
			context.Response.Output.Write("<h1>HTTP " + code.ToString() + " - " + message.Replace("<", "&lt;").Replace(">", "&gt;") + "</h1>\r\n");
			context.Response.Output.Write("<hr/>\r\n");
			context.Response.Output.Write("<div>Type: " + type + (!string.IsNullOrWhiteSpace(correlationID) ? " - Correlation ID: " + correlationID : "") + "</div>\r\n");
			if (!string.IsNullOrWhiteSpace(stack) && showStack)
				context.Response.Output.Write("<div><br/>Stack:</div>\r\n<blockquote>" + stack.Replace("<", "&lt;").Replace(">", "&gt;").Replace("\n", "<br/>").Replace("\r", "").Replace("\t", "") + "</blockquote>\r\n");
			context.Response.Output.Write("</body>\r\n</html>");

			if (message.IsContains("potentially dangerous"))
				context.Response.End();
		}

		/// <summary>
		/// Show HTTP error
		/// </summary>
		/// <param name="context"></param>
		/// <param name="code"></param>
		/// <param name="message"></param>
		/// <param name="type"></param>
		/// <param name="correlationID"></param>
		/// <param name="stack"></param>
		/// <param name="showStack"></param>
		public static async Task ShowHttpErrorAsync(this HttpContext context, int code, string message, string type, string correlationID = null, string stack = null, bool showStack = true)
		{
			code = code < 1 ? (int)HttpStatusCode.InternalServerError : code;

			context.Response.TrySkipIisCustomErrors = true;
			context.Response.StatusCode = code;
			context.Response.Cache.SetNoStore();
			context.Response.ContentType = "text/html";

			context.Response.ClearContent();
			await context.Response.Output.WriteAsync("<!DOCTYPE html>\r\n").ConfigureAwait(false);
			await context.Response.Output.WriteAsync("<html xmlns=\"http://www.w3.org/1999/xhtml\">\r\n").ConfigureAwait(false);
			await context.Response.Output.WriteAsync("<head><title>Error " + code.ToString() + "</title></head>\r\n<body>\r\n").ConfigureAwait(false);
			await context.Response.Output.WriteAsync("<h1>HTTP " + code.ToString() + " - " + message.Replace("<", "&lt;").Replace(">", "&gt;") + "</h1>\r\n").ConfigureAwait(false);
			await context.Response.Output.WriteAsync("<hr/>\r\n").ConfigureAwait(false);
			await context.Response.Output.WriteAsync("<div>Type: " + type + (!string.IsNullOrWhiteSpace(correlationID) ? " - Correlation ID: " + correlationID : "") + "</div>\r\n").ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(stack) && showStack)
				await context.Response.Output.WriteAsync("<div><br/>Stack:</div>\r\n<blockquote>" + stack.Replace("<", "&lt;").Replace(">", "&gt;").Replace("\n", "<br/>").Replace("\r", "").Replace("\t", "") + "</blockquote>\r\n").ConfigureAwait(false);
			await context.Response.Output.WriteAsync("</body>\r\n</html>").ConfigureAwait(false);

			if (message.IsContains("potentially dangerous"))
				context.Response.End();
		}
		#endregion

	}
}