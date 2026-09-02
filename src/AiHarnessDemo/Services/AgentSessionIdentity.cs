using System.Security.Cryptography;
using System.Text;

namespace AiHarnessDemo.Services;

internal static class AgentSessionIdentity
{
    public static Guid Create(Guid flowId, int iteration, string agentId)
    {
        var input = Encoding.UTF8.GetBytes(
            $"{flowId:N}:{iteration}:{agentId.Trim().ToLowerInvariant()}");
        var hash = SHA256.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        var hex = Convert.ToHexString(hash.AsSpan(0, 16));
        return Guid.ParseExact(
            $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}",
            "D");
    }
}
