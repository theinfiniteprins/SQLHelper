using System.Security.Cryptography;
using System.Text;
using SqlHelper.Core.Credentials;

namespace SqlHelper.Core.Tests.Credentials;

public sealed class DpapiSecretProtectorTests
{
    [Fact]
    public void Protect_then_Unprotect_round_trips_a_unicode_password()
    {
        var protector = new DpapiSecretProtector();
        const string password = "Pä$$w0rd — 日本語 — 🔐";

        byte[] blob = protector.Protect(password);
        string recovered = protector.Unprotect(blob);

        Assert.Equal(password, recovered);
        Assert.False(blob.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(password)));
    }

    [Fact]
    public void Blob_from_one_entropy_cannot_be_read_with_another()
    {
        var a = new DpapiSecretProtector(MasterKey.DeriveEntropy("passphrase-A"));
        var b = new DpapiSecretProtector(MasterKey.DeriveEntropy("passphrase-B"));

        byte[] blob = a.Protect("secret");

        Assert.Throws<CryptographicException>(() => b.Unprotect(blob));
    }

    [Fact]
    public void ProtectBytes_round_trips_binary_including_zero_bytes()
    {
        var protector = new DpapiSecretProtector();
        byte[] payload = [0x00, 0x01, 0xFF, 0x00, 0x7F, 0x80, 0x00];

        byte[] recovered = protector.UnprotectBytes(protector.ProtectBytes(payload));

        Assert.Equal(payload, recovered);
    }

    [Fact]
    public void Passphrase_entropy_still_lets_the_same_instance_read_its_own_blob()
    {
        byte[] entropy = MasterKey.DeriveEntropy("correct horse battery staple");
        var protector = new DpapiSecretProtector(entropy);

        Assert.Equal("hunter2", protector.Unprotect(protector.Protect("hunter2")));
    }
}
