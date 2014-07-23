﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using uhttpsharp.Headers;

namespace uhttpsharp.Handlers
{
    public class CompressionHandler : IHttpRequestHandler
    {
        private readonly IEnumerable<ICompressor> _compressors;
        private static readonly char[] Seperator = new[] { ',' };
        public CompressionHandler(params ICompressor[] compressors)
        {
            _compressors = compressors;
        }

        public async Task Handle(IHttpContext context, Func<Task> next)
        {
            await next();

            if (context.Response == null)
            {
                return;
            }

            var encodings = context.Request.Headers.GetByName("Accept-Encoding")
                .Split(Seperator, StringSplitOptions.RemoveEmptyEntries);

            var compressor =
                _compressors.FirstOrDefault(c => encodings.Contains(c.Name, StringComparer.InvariantCultureIgnoreCase));

            if (compressor == null)
            {
                return;
            }

            context.Response = await compressor.Compress(context.Response).ConfigureAwait(false);
        }
    }

    public interface ICompressor
    {

        string Name { get; }

        Task<IHttpResponse> Compress(IHttpResponse response);

    }

    public class DeflateCompressor : ICompressor
    {
        public static readonly ICompressor Default = new DeflateCompressor();

        public string Name
        {
            get { return "deflate"; }
        }
        public Task<IHttpResponse> Compress(IHttpResponse response)
        {
            return CompressedResponse.CreateDeflate(response);
        }
    }

    public class GZipCompressor : ICompressor
    {
        public static readonly ICompressor Default = new GZipCompressor();

        public string Name
        {
            get { return "gzip"; }
        }
        public Task<IHttpResponse> Compress(IHttpResponse response)
        {
            return CompressedResponse.CreateGZip(response);
        }
    }

    public class DeflateHandler : IHttpRequestHandler
    {
        public async Task Handle(IHttpContext context, Func<Task> next)
        {
            await next();

            if (context.Response != null)
            {
                context.Response = await CompressedResponse.CreateDeflate(context.Response).ConfigureAwait(false);
            }
        }
    }

    public class CompressedResponse : IHttpResponse
    {
        private readonly HttpResponseCode _responseCode;
        private readonly IHttpHeaders _headers;
        private readonly MemoryStream _memoryStream;
        private readonly bool _closeConnection;

        public CompressedResponse(IHttpResponse child, MemoryStream memoryStream, string encoding)
        {
            _memoryStream = memoryStream;

            _responseCode = child.ResponseCode;
            _closeConnection = child.CloseConnection;
            _headers =
                new ListHttpHeaders(
                    child.Headers.Where(h => !h.Key.Equals("content-length", StringComparison.InvariantCultureIgnoreCase))
                        .Concat(new[]
                        {
                            new KeyValuePair<string, string>("content-length", memoryStream.Length.ToString(CultureInfo.InvariantCulture)),
                            new KeyValuePair<string, string>("content-encoding", encoding), 
                        })
                        .ToList());


        }


        public static async Task<IHttpResponse> Create(IHttpResponse child, Func<Stream, Stream> streamFactory)
        {
            var memoryStream = new MemoryStream();
            using (var deflateStream = streamFactory(memoryStream))
            using (var deflateWriter = new StreamWriter(deflateStream))
            {
                await child.WriteBody(deflateWriter).ConfigureAwait(false);
                await deflateWriter.FlushAsync();
            }

            return new CompressedResponse(child, memoryStream, "deflate");
        }

        public static Task<IHttpResponse> CreateDeflate(IHttpResponse child)
        {
            return Create(child, s => new DeflateStream(s, CompressionMode.Compress, true));
        }

        public static Task<IHttpResponse> CreateGZip(IHttpResponse child)
        {
            return Create(child, s => new GZipStream(s, CompressionMode.Compress, true));
        }

        public async Task WriteBody(StreamWriter writer)
        {
            _memoryStream.Position = 0;

            await _memoryStream.CopyToAsync(writer.BaseStream).ConfigureAwait(false);
        }
        public HttpResponseCode ResponseCode
        {
            get { return _responseCode; }
        }
        public IHttpHeaders Headers
        {
            get { return _headers; }
        }
        public bool CloseConnection
        {
            get { return _closeConnection; }
        }
    }
}
