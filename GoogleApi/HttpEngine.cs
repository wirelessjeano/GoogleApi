﻿using GoogleApi.Entities;
using GoogleApi.Entities.Common.Enums;
using GoogleApi.Entities.Interfaces;
using GoogleApi.Exceptions;
using Newtonsoft.Json;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GoogleApi
{
    /// <summary>
    /// Http Engine (abstract).
    /// </summary>
    public abstract class HttpEngine : IDisposable
    {
        private static HttpClient httpClient;

        /// <summary>
        /// Http Client.
        /// </summary>
        protected internal static HttpClient HttpClient
        {
            get
            {
                if (HttpEngine.httpClient == null)
                {
                    var httpClientHandler = new HttpClientHandler
                    {
                        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                    };

                    HttpEngine.httpClient = new HttpClient(httpClientHandler)
                    {
                        Timeout = HttpEngine.HttpTimeout
                    };

                    HttpEngine.httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                }

                return HttpEngine.httpClient;
            }
            set
            {
                HttpEngine.httpClient = value;
            }
        }

        /// <summary>
        /// Http Timeout.
        /// The http timeout for the request to receive a response.
        /// </summary>
        protected internal static TimeSpan HttpTimeout { get; set; } = new TimeSpan(0, 0, 30);

        /// <summary>
        /// Always Dispose.
        /// When true the <see cref="HttpClient"/> will be disposed after each execution.
        /// Otherwise it's re-used on following invocations.
        /// </summary>
        public static bool AlwaysDispose { get; set; } = false;

        /// <summary>
        /// Disposes.
        /// </summary>
        public virtual void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes of the <see cref="HttpClient"/>, if <paramref name="disposing"/> is true.
        /// </summary>
        /// <param name="disposing">Whether to dispose resources or not.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
                return;

            HttpEngine.HttpClient?.Dispose();
            HttpEngine.httpClient = null;
        }
    }

    /// <summary>
    /// Http Engine.
    /// Manges the http connections, and is responsible for invoking requst and handling responses.
    /// </summary>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    public sealed class HttpEngine<TRequest, TResponse> : HttpEngine
        where TRequest : IRequest, new()
        where TResponse : IResponse, new()
    {
        internal static readonly HttpEngine<TRequest, TResponse> instance = new HttpEngine<TRequest, TResponse>();

        /// <summary>
        /// Query.
        /// </summary>
        /// <param name="request">The request that will be sent.</param>
        /// <returns>The <see cref="IResponse"/>.</returns>
        public TResponse Query(TRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            return this.QueryAsync(request).Result;
        }

        /// <summary>
        /// Query Async.
        /// </summary>
        /// <param name="request">The request that will be sent.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
        /// <returns>The <see cref="Task{T}"/>.</returns>
        public async Task<TResponse> QueryAsync(TRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (cancellationToken == null)
                throw new ArgumentNullException(nameof(cancellationToken));

            var taskCompletion = new TaskCompletionSource<TResponse>();

            try
            {
                await this.ProcessRequest(request, cancellationToken)
                    .ContinueWith(async x =>
                    {
                        try
                        {
                            if (x.IsCanceled)
                            {
                                taskCompletion.SetCanceled();
                            }
                            else if (x.IsFaulted)
                            {
                                throw x.Exception ?? new Exception();
                            }
                            else
                            {
                                var result = await x;
                                var response = await this.ProcessResponse(result);

                                result.EnsureSuccessStatusCode();

                                switch (response.Status)
                                {
                                    case Status.Ok:
                                    case Status.ZeroResults:
                                        taskCompletion.SetResult(response);
                                        break;

                                    default:
                                        throw new InvalidOperationException(response.ErrorMessage);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            var exception = ex.GetBaseException();
                            var googleException = new GoogleApiException(exception.Message, exception);

                            taskCompletion.SetException(googleException);
                        }
                    }, cancellationToken);
            }
            finally
            {
                if (HttpEngine.AlwaysDispose)
                {
                    HttpEngine.HttpClient.Dispose();
                    HttpEngine.HttpClient = null;
                }
            }

            return await taskCompletion.Task;
        }

        private async Task<HttpResponseMessage> ProcessRequest(TRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            var uri = request.GetUri();

            if (request is IRequestQueryString)
            {
                return await HttpEngine.HttpClient.GetAsync(uri, cancellationToken);
            }
            else
            {
                var settings = new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                };
                var serializeObject = JsonConvert.SerializeObject(request, settings);
                var stringContent = new StringContent(serializeObject, Encoding.UTF8);
                var content = await stringContent.ReadAsStreamAsync();
                var streamContent = new StreamContent(content);

                return await HttpEngine.HttpClient.PostAsync(uri, streamContent, cancellationToken);
            }
        }
        private async Task<TResponse> ProcessResponse(HttpResponseMessage httpResponse)
        {
            if (httpResponse == null)
                throw new ArgumentNullException(nameof(httpResponse));

            var response = new TResponse();
            
            switch (response)
            {
                case BaseStreamResponse streamResponse:
                    streamResponse.Buffer = await httpResponse.Content.ReadAsByteArrayAsync();
                    response = (TResponse)(IResponse)streamResponse;
                    break;

                default:
                    var rawJson = await httpResponse.Content.ReadAsStringAsync();
                    response = JsonConvert.DeserializeObject<TResponse>(rawJson);
                    response.RawJson = rawJson;
                    break;
            }

            response.RawQueryString = httpResponse.RequestMessage.RequestUri.PathAndQuery;
            response.Status = httpResponse.IsSuccessStatusCode
                ? response.Status ?? Status.Ok
                : Status.HttpError;

            return response;
        }
    }
}