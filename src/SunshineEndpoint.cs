using System;

namespace SunshineWindowFollow
{
    /// <summary>
    /// 规范化后的 Sunshine 端点：协议 + 主机 + 端口（忽略用户填写的路径），以及凭据。
    /// 端点契约固定为 {BaseUri}/api/capture/window-follow。
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
            get { return BaseUri != null && BaseUri.IsLoopback; }
        }

        /// <summary>
        /// 从插件设置创建端点。地址允许省略 "https://" 前缀；路径部分会被忽略。
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
                Username = settings.Username ?? string.Empty,
                Password = settings.Password ?? string.Empty
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

            return string.Equals(requestUri.Scheme, BaseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(requestUri.Host, BaseUri.Host, StringComparison.OrdinalIgnoreCase) &&
                   requestUri.Port == BaseUri.Port;
        }
    }
}
