using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SunshineWindowFollow.ProtocolTests
{
    internal sealed class CapturedRequest
    {
        public string Method { get; set; }

        public string Path { get; set; }

        public string ContentType { get; set; }

        public string Authorization { get; set; }

        public string Body { get; set; }
    }

    internal sealed class TestReply
    {
        public int StatusCode { get; set; } = 200;

        public string ContentType { get; set; } = "application/json";

        public string Body { get; set; } = string.Empty;

        public string Location { get; set; }

        public static TestReply Json(string body)
        {
            return new TestReply { StatusCode = 200, ContentType = "application/json", Body = body };
        }

        public static TestReply Status(int statusCode, string body = "", string location = null)
        {
            return new TestReply
            {
                StatusCode = statusCode,
                ContentType = "text/plain",
                Body = body,
                Location = location
            };
        }
    }

    /// <summary>
    /// 可复现的本地 HTTP/HTTPS 服务：记录请求，返回固定响应。
    /// HTTPS 使用运行时生成的自签名证书（CN=localhost，与 127.0.0.1 不匹配），
    /// 用于验证插件只对回环端点放行自签名证书。
    /// </summary>
    internal sealed class TestServer : IAsyncDisposable
    {
        private readonly WebApplication app;

        private TestServer(WebApplication app, Uri baseUri, ConcurrentQueue<CapturedRequest> requests)
        {
            this.app = app;
            BaseUri = baseUri;
            Requests = requests;
        }

        public Uri BaseUri { get; }

        public ConcurrentQueue<CapturedRequest> Requests { get; }

        public static async Task<TestServer> StartAsync(bool https, Func<CapturedRequest, TestReply> handler)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(TestServer).Assembly.GetName().Name,
                ContentRootPath = Directory.GetCurrentDirectory()
            });

            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0, listen =>
                {
                    if (https)
                    {
                        listen.UseHttps(CreateSelfSignedCertificate());
                    }
                });
            });

            var app = builder.Build();
            var requests = new ConcurrentQueue<CapturedRequest>();
            app.Run(async context =>
            {
                string body;
                using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync();
                }

                var captured = new CapturedRequest
                {
                    Method = context.Request.Method,
                    Path = context.Request.Path.Value ?? string.Empty,
                    ContentType = context.Request.ContentType,
                    Authorization = context.Request.Headers.Authorization.ToString(),
                    Body = body
                };
                requests.Enqueue(captured);

                var reply = handler == null ? TestReply.Json("{\"ok\":true}") : handler(captured);
                context.Response.StatusCode = reply.StatusCode;
                if (!string.IsNullOrEmpty(reply.Location))
                {
                    context.Response.Headers.Location = reply.Location;
                }

                context.Response.ContentType = reply.ContentType;
                await context.Response.WriteAsync(reply.Body ?? string.Empty);
            });

            await app.StartAsync();

            var addresses = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                .Addresses;
            var baseUri = new Uri(addresses.First().TrimEnd('/') + "/");

            return new TestServer(app, baseUri, requests);
        }

        public static X509Certificate2 CreateSelfSignedCertificate()
        {
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    "CN=localhost",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, false));
                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                        false));
                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                        false));

                // Deliberately no SAN/IP: the certificate is both self-signed and
                // name-mismatched when connecting to 127.0.0.1, like a default
                // Sunshine self-signed certificate accessed by IP.
                using (var certificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddYears(1)))
                {
                    return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    await app.StopAsync(timeout.Token);
                }
            }
            finally
            {
                await app.DisposeAsync();
            }
        }
    }
}
