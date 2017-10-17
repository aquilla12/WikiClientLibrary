﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WikiClientLibrary.Infrastructures;
using WikiClientLibrary.Infrastructures.Logging;

namespace WikiClientLibrary.Client
{
    /// <summary>
    /// Provides basic operations for MediaWiki API via HTTP(S).
    /// </summary>
    public class WikiClient : IWikiClient, IWikiClientLoggable, IDisposable
    {
        /// <summary>
        /// The User Agent of Wiki Client Library.
        /// </summary>
        public const string WikiClientUserAgent = "WikiClientLibrary/0.6 (.NET Portable; http://github.com/cxuesong/WikiClientLibrary)";

        public WikiClient() : this(new HttpClientHandler(), true)
        {
            _HttpClientHandler.UseCookies = true;
            // https://www.mediawiki.org/wiki/API:Client_code
            // Please use GZip compression when making API calls (Accept-Encoding: gzip).
            // Bots eat up a lot of bandwidth, which is not free.
            if (_HttpClientHandler.SupportsAutomaticDecompression)
            {
                _HttpClientHandler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            }
        }

        /// <param name="handler">The HttpMessageHandler responsible for processing the HTTP response messages.</param>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <c>null</c>.</exception>
        public WikiClient(HttpMessageHandler handler) : this(handler, true)
        {
        }

        /// <param name="handler">The HttpMessageHandler responsible for processing the HTTP response messages.</param>
        /// <param name="disposeHandler">Whether to automatically dispose the handler when disposing this Client.</param>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <c>null</c>.</exception>
        public WikiClient(HttpMessageHandler handler, bool disposeHandler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            HttpClient = new HttpClient(handler, disposeHandler);
            ClientUserAgent = null;
            _HttpClientHandler = handler as HttpClientHandler;
#if DEBUG
            HttpClient.DefaultRequestHeaders.Add("X-WCL-DEBUG-CLIENT-ID", GetHashCode().ToString());
#endif
        }

        #region Configurations

        private string _ClientUserAgent;
        private readonly HttpClientHandler _HttpClientHandler;
        private int _MaxRetries = 3;
        private ILogger _Logger = NullLogger.Instance;

        /// <summary>
        /// Timeout for each query.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Delay before each retry.
        /// </summary>
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Max retries count.
        /// </summary>
        public int MaxRetries
        {
            get { return _MaxRetries; }
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _MaxRetries = value;
            }
        }

        /// <summary>
        /// User Agent for client-side application.
        /// </summary>
        public string ClientUserAgent
        {
            get { return _ClientUserAgent; }
            set
            {
                if (_ClientUserAgent != value)
                {
                    var ua = HttpClient.DefaultRequestHeaders.UserAgent;
                    if (!string.IsNullOrWhiteSpace(value))
                        ua.ParseAdd(value);
                    ua.ParseAdd(WikiClientUserAgent);
                    _ClientUserAgent = value;
                }
            }
        } 

        /// <summary>
        /// Referer.
        /// </summary>
        public string Referer { get; set; }

        /// <summary>
        /// Gets/Sets the cookies used in the requests.
        /// </summary>
        /// <remarks>
        /// <para>To persist user's login information, you can persist the value of this property.</para>
        /// <para>You can use the same CookieContainer with different <see cref="WikiClient"/>s.</para>
        /// </remarks>
        /// <exception cref="NotSupportedException">You have initialized this Client with a HttpMessageHandler that is not a HttpClientHandler.</exception>
        public CookieContainer CookieContainer
        {
            get { return _HttpClientHandler.CookieContainer; }
            set
            {
                if (_HttpClientHandler == null)
                    throw new NotSupportedException("Not supported when working with a HttpMessageHandler that is not a HttpClientHandler.");
                _HttpClientHandler.CookieContainer = value;
            }
        }

        internal HttpClient HttpClient { get; }

        /// <inheritdoc />
        public ILogger Logger
        {
            get => _Logger;
            set => _Logger = value ?? NullLogger.Instance;
        }

        #endregion

        private static readonly KeyValuePair<string, object>[] formatJsonKeyValue =
        {
            new KeyValuePair<string, object>("format", "json")
        };

        /// <inheritdoc />
        public async Task<object> InvokeAsync(string endPointUrl, WikiRequestMessage message,
            IWikiResponseMessageParser responseParser, CancellationToken cancellationToken)
        {
            if (endPointUrl == null) throw new ArgumentNullException(nameof(endPointUrl));
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (message is MediaWikiFormRequestMessage form)
                message = new MediaWikiFormRequestMessage(form.Id, form, formatJsonKeyValue, false);
            using (this.BeginActionScope(null, message))
            {
                var result = await SendAsync(endPointUrl, message, responseParser, cancellationToken);
                return result;
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{GetType().Name}#{GetHashCode()}";
        }

        protected virtual HttpRequestMessage CreateHttpRequestMessage(string endPointUrl, WikiRequestMessage message)
        {
            var url = endPointUrl;
            var query = message.GetHttpQuery();
            if (query != null) url = url + "?" + query;
            return new HttpRequestMessage(message.GetHttpMethod(), url)
                {Content = message.GetHttpContent()};
        }

        private async Task<object> SendAsync(string endPointUrl, WikiRequestMessage message,
            IWikiResponseMessageParser responseParser, CancellationToken cancellationToken)
        {
            Debug.Assert(endPointUrl != null);
            Debug.Assert(message != null);

            var httpRequest = CreateHttpRequestMessage(endPointUrl, message);
            var retries = 0;

            async Task<bool> PrepareForRetry(TimeSpan delay)
            {
                if (retries >= MaxRetries) return false;
                retries++;
                try
                {
                    httpRequest = CreateHttpRequestMessage(endPointUrl, message);
                }
                catch (Exception ex) when (ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                    // Some content (e.g. StreamContent with un-seekable Stream) may throw this exception
                    // on the second try.
                    Logger.LogWarning("Cannot retry: {Exception}.", ex.Message);
                    return false;
                }
                Logger.LogDebug("Retry #{Retries} after {Delay}.", retries, RetryDelay);
                await Task.Delay(delay, cancellationToken);
                return true;
            }

            RETRY:
            Logger.LogTrace("Initiate request to: {EndPointUrl}.", endPointUrl);
            cancellationToken.ThrowIfCancellationRequested();
            var requestSw = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                // Use await instead of responseTask.Result to unwrap Exceptions.
                // Or AggregateException might be thrown.
                using (var responseCancellation = new CancellationTokenSource(Timeout))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, responseCancellation.Token))
                    response = await HttpClient.SendAsync(httpRequest, linkedCts.Token);
                // The request has been finished.
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Logger.LogWarning("Cancelled via CancellationToken.");
                    cancellationToken.ThrowIfCancellationRequested();
                }
                Logger.LogWarning("Timeout.");
                if (!await PrepareForRetry(RetryDelay)) throw new TimeoutException();
                goto RETRY;
            }
            using (response)
            {
                // Validate response.
                var statusCode = (int)response.StatusCode;
                Logger.LogTrace("HTTP {StatusCode}, elapsed: {Time}", statusCode, requestSw.Elapsed);
                if (!response.IsSuccessStatusCode)
                    Logger.LogWarning("HTTP {StatusCode} {Reason}.", statusCode, response.ReasonPhrase, requestSw.Elapsed);
                if (statusCode >= 500 && statusCode <= 599)
                {
                    // Service Error. We can retry.
                    // HTTP 503 : https://www.mediawiki.org/wiki/Manual:Maxlag_parameter
                    if (retries < MaxRetries)
                    {
                        // Delay per Retry-After Header
                        var date = response.Headers.RetryAfter?.Date;
                        var delay = response.Headers.RetryAfter?.Delta;
                        if (delay == null && date != null) delay = date - DateTimeOffset.Now;
                        // Or use the default delay
                        if (delay == null) delay = RetryDelay;
                        else if (delay > RetryDelay) delay = RetryDelay;
                        if (await PrepareForRetry(delay.Value)) goto RETRY;
                    }
                }
                // For HTTP 500~599, EnsureSuccessStatusCode will throw an Exception.
                response.EnsureSuccessStatusCode();
                cancellationToken.ThrowIfCancellationRequested();
                var context = new WikiResponseParsingContext(Logger, cancellationToken);
                try
                {
                    var parsed = await responseParser.ParseResponseAsync(response, context);
                    if (context.NeedRetry)
                    {
                        if (await PrepareForRetry(RetryDelay)) goto RETRY;
                        throw new InvalidOperationException("Reached maximum count of retries.");
                    }
                    return parsed;
                }
                catch (Exception ex)
                {
                    if (context.NeedRetry && await PrepareForRetry(RetryDelay))
                    {
                        Logger.LogWarning("{Parser}: {Message}", responseParser, ex.Message);
                        goto RETRY;
                    }
                    Logger.LogWarning(new EventId(), ex, "Parser {Parser} throws an Exception.", responseParser);
                    throw;
                }
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                HttpClient.Dispose();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
