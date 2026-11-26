using AiNative.Server.Protocol.Messages;

namespace AiNative.Server.Transport
{
    /// <summary>部署清单中的精确叶证书 SHA-256 身份；不包含私钥。更换证书须更新部署配置。</summary>
    public sealed record ServerCertificateIdentity(string ServiceId, ServerRole Role, string Sha256Fingerprint);
}
