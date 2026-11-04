using System;
using System.Net;

namespace SunshineWindowFollow
{
    /// <summary>
    /// 规范化后的 Sunshine 端点：协议 + 主机 + 端口（忽略用户填写的路径），以及凭据。
    /// 端点契约固定为 {BaseUri}/api/capture/window-follow。
    ///
    /// 安全边界：Sunshine 的该控制端点只接受本机（localhost）请求，因此这里也
    /// 只允许回环地址，避免把凭据或控制请求发到局域网/公网。HTTP 仅作为本机
    /// 调试用途保留，且禁止在明文 HTTP 上携带用户名/密码。
    /// </summary>
    public sealed class SunshineEndpoint
    {
        public Uri BaseUri { get; private set; }

        public Uri ApiUri { get; private set; }

        public string Username { get; private set; }

        public string Password { get; private set; }

        /// <summary>用于日志展示的地址（不含凭据）。</summary>
        public string DisplayUrl
        {
            get { return BaseUri == null ? string.Empty : BaseUri.GetLeftPart(UriPartial.Authority); }
        }

        /// <summary>目标主机是否是回环地址（Sunshine 默认与 Playnite 在同一台机器）。</summary>
        public bool IsLoopbackHost
        {
            get { return IsLoopbackUri(BaseUri); }
        }

        /// <summary>是否使用了明文的 http://（仅允许无凭据的本机调试）。</summary>
        public bool IsInsecureHttp
        {
            get
            {
                return BaseUri != null &&
                       string.Equals(BaseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>是否配置了 HTTP Basic 凭据。</summary>
        public bool HasCredentials
        {
            get { return !string.IsNullOrEmpty(Username) || !string.IsNullOrEmpty(Password); }
        }

        /// <summary>
        /// 从插件设置创建端点。地址允许省略 "https://" 前缀；路径部分会被忽略。
        /// 只接受回环主机；http:// 仅在未配置凭据时允许。
        /// </summary>
        public static bool TryCreate(SunshineSettings settings, out SunshineEndpoint endpoint, out string error)
        {
            endpoint = null;
            error = null;

            if (settings == null)
            {
                error = "插件设置尚未加载";
                return false;
            }

            var raw = (settings.ServerUrl ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                error = "Sunshine 地址为空";
                return false;
            }

            if (raw.IndexOf("://", StringComparison.Ordinal) < 0)
            {
                raw = "https://" + raw;
            }

            Uri parsed;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out parsed))
            {
                error = "无法解析地址：" + settings.ServerUrl;
                return false;
            }

            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                error = "只支持 http:// 或 https:// 地址，当前为：" + parsed.Scheme;
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.Host))
            {
                error = "地址缺少主机名";
                return false;
            }

            if (!string.IsNullOrEmpty(parsed.UserInfo))
            {
                error = "地址中不要包含用户名或密码；请在插件设置中单独填写 WebUI 凭据";
                return false;
            }

            if (!IsLoopbackUri(parsed))
            {
                error = "Sunshine 控制端点只允许本机回环地址（127.0.0.1、::1 或 localhost），当前为：" +
                        parsed.Host;
                return false;
            }

            var username = settings.Username ?? string.Empty;
            var password = settings.Password ?? string.Empty;
            if (string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                (username.Length > 0 || password.Length > 0))
            {
                error = "不允许通过明文 http:// 发送 Sunshine 凭据；请使用 https://127.0.0.1:47990，或清空用户名/密码";
                return false;
            }

            Uri baseUri;
            try
            {
                // 只保留 scheme + host + port，忽略用户可能误填的路径/查询串。
                var authority = parsed.GetLeftPart(UriPartial.Authority);
                baseUri = new Uri(authority.TrimEnd('/') + "/", UriKind.Absolute);
            }
            catch (Exception exception)
            {
                error = "地址无效：" + exception.Message;
                return false;
            }

            endpoint = new SunshineEndpoint
            {
                BaseUri = baseUri,
                ApiUri = new Uri(baseUri, "api/capture/window-follow"),
                Username = username,
                Password = password
            };
            return true;
        }

        /// <summary>判断某个实际请求的 URI 是否指向本端点（用于限定自签名证书放行范围）。</summary>
        public bool MatchesRequestUri(Uri requestUri)
        {
            if (requestUri == null || BaseUri == null)
            {
                return false;
            }

            return IsLoopbackUri(requestUri) &&
                   string.Equals(requestUri.Scheme, BaseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(requestUri.Host, BaseUri.Host, StringComparison.OrdinalIgnoreCase) &&
                   requestUri.Port == BaseUri.Port;
        }

        internal static bool IsLoopbackUri(Uri uri)
        {
            if (uri == null)
            {
                return false;
            }

            if (uri.IsLoopback)
            {
                return true;
            }

            IPAddress address;
            if (IPAddress.TryParse(uri.Host, out address))
            {
                return IPAddress.IsLoopback(address);
            }

            return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        }
    }
}
