using DSC.TLink.ITv2.Encryption;
using DSC.TLink.Tests.Reference;

namespace DSC.TLink.Tests;

/// <summary>
/// Pure encryption functions: known-answer vectors, plus equivalence with the stateful
/// EncryptionHandler classes that talk to real panels today. Deterministic: initializers are
/// fixed bytes, never random.
/// </summary>
public class ITv2CryptoTests
{
    private static readonly byte[] Type2AccessCode = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
    private static readonly byte[] Initializer16 = Convert.FromHexString("0F1E2D3C4B5A69788796A5B4C3D2E1F0");

    /// <summary>FIPS-197 appendix C.1 (AES-128).</summary>
    [Fact]
    public void Aes128_MatchesFips197Vector()
    {
        var key = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        var plain = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");

        Assert.Equal("69C4E0D86A7B0430D8CDB78070B4C55A", Convert.ToHexString(ITv2Crypto.Encrypt(key, plain)));
        Assert.Equal(plain, ITv2Crypto.Decrypt(key, ITv2Crypto.Encrypt(key, plain)));
    }

    [Fact]
    public void ZeroPadding_PadsToTheBlockSize_AndDecryptsWithTrailingZeros()
    {
        var key = ITv2Crypto.Type2.InboundKey(Type2AccessCode, Initializer16);
        var packet = Convert.FromHexString("0801000502570062CE");   // a real 9-byte handshake frame

        var cipher = ITv2Crypto.Encrypt(key, packet);
        var plain = ITv2Crypto.Decrypt(key, cipher);

        Assert.Equal(16, cipher.Length);
        Assert.Equal(packet, plain[..packet.Length]);
        Assert.All(plain[packet.Length..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void SameInputs_SameKeys()
        => Assert.Equal(
            ITv2Crypto.Type2.OutboundKey(Type2AccessCode, Initializer16),
            ITv2Crypto.Type2.OutboundKey(Type2AccessCode, Initializer16));

    /// <summary>
    /// The existing handler (used against real panels) and the pure functions derive the same
    /// keys: data encrypted one way decrypts the other.
    /// </summary>
    [Fact]
    public void Type2_MatchesTheExistingHandler()
    {
        using var handler = new Type2EncryptionHandler(Convert.ToHexString(Type2AccessCode));
        var data = Convert.FromHexString("0C272709007C0101010000E8C5");

        // Outbound: we encrypt with the key derived from the panel's initializer.
        handler.ConfigureOutboundEncryption(Initializer16);
        Assert.Equal(handler.HandleOutboundData(data),
            ITv2Crypto.Encrypt(ITv2Crypto.Type2.OutboundKey(Type2AccessCode, Initializer16), data));

        // Inbound: the handler picks a random initializer; the pure key from it must decrypt.
        var localInitializer = handler.ConfigureInboundEncryption();
        var inboundKey = ITv2Crypto.Type2.InboundKey(Type2AccessCode, localInitializer);
        Assert.Equal(data, handler.HandleInboundData(ITv2Crypto.Encrypt(inboundKey, data))[..data.Length]);
    }

    [Fact]
    public void Type1_InitializerWeSend_IsAcceptedByThePanelSideCheck()
    {
        const string accessCode = "12345678";
        var random = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray();

        // We build our initializer with the access code...
        var (inboundKey, initializer) = ITv2Crypto.Type1.Inbound(ITv2Crypto.Type1.ExpandKeyString(accessCode), random);

        // ...and the panel validates it the same way we validate the panel's, with that key.
        var keyFromPanelSide = ITv2Crypto.Type1.OutboundKey(ITv2Crypto.Type1.ExpandKeyString(accessCode), initializer);

        Assert.Equal(48, initializer.Length);
        Assert.Equal(inboundKey, keyFromPanelSide);
        Assert.Equal(16, inboundKey.Length);
    }

    [Fact]
    public void Type1_TamperedInitializer_FailsTheCheck()
    {
        var key = ITv2Crypto.Type1.ExpandKeyString("12345678");
        var (_, initializer) = ITv2Crypto.Type1.Inbound(key, new byte[32]);
        initializer[0] ^= 0xFF;

        Assert.Null(ITv2Crypto.Type1.OutboundKey(key, initializer));
    }

    [Fact]
    public void Type1_MatchesTheExistingHandler()
    {
        const string accessCode = "12345678";
        const string identification = "20000000";
        using var handler = new Type1EncryptionHandler(accessCode, identification);

        // Our side of the handshake, from the handler (random), must agree with the pure key.
        var initializer = handler.ConfigureInboundEncryption();
        var data = Convert.FromHexString("0801000502570062CE");
        var inboundKey = ITv2Crypto.Type1.OutboundKey(ITv2Crypto.Type1.ExpandKeyString(accessCode), initializer)!;
        Assert.Equal(data, handler.HandleInboundData(ITv2Crypto.Encrypt(inboundKey, data))[..data.Length]);

        // The panel's side: an initializer built with the identification number.
        var (panelKey, panelInitializer) = ITv2Crypto.Type1.Inbound(ITv2Crypto.Type1.ExpandKeyString(identification),
            Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray());
        handler.ConfigureOutboundEncryption(panelInitializer);
        Assert.Equal(handler.HandleOutboundData(data), ITv2Crypto.Encrypt(panelKey, data));
    }
}
