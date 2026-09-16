using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MailWinnow.Infrastructure.Outgoing;

public sealed class OutgoingPayloadProtection(IDataProtectionProvider provider)
{
    private IDataProtector Protector(string owner, string kind, Guid id) => provider.CreateProtector("MailWinnow.Outgoing.v1", owner, kind, id.ToString("N"));
    public string Protect<T>(string owner, string kind, Guid id, T value) => Protector(owner, kind, id).Protect(JsonSerializer.Serialize(value));
    public T Unprotect<T>(string owner, string kind, Guid id, string value) => JsonSerializer.Deserialize<T>(Protector(owner, kind, id).Unprotect(value))!;
    public byte[] ProtectBytes(string owner, string kind, Guid id, byte[] value) => Protector(owner, kind, id).Protect(value);
    public byte[] UnprotectBytes(string owner, string kind, Guid id, byte[] value) => Protector(owner, kind, id).Unprotect(value);
}
