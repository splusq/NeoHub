using System.Security.Cryptography;

namespace DSC.TLink.ITv2.Encryption;

/// <summary>
/// ITv2 encryption as pure functions. Packets are AES-128/ECB with zero padding; each
/// direction has its own key, derived during the RequestAccess exchange from the integration
/// access code and an initializer chosen by the receiving side.
///
/// Unlike <see cref="EncryptionHandler"/>, nothing here holds state or reads a random source:
/// the caller supplies the local initializer (random in production, fixed in tests), and every
/// call uses its own cipher instance, so these are safe to call from anywhere and give the same
/// answer for the same input.
/// </summary>
internal static class ITv2Crypto
{
    public static byte[] Encrypt(byte[] key, ReadOnlySpan<byte> plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptEcb(plaintext, PaddingMode.Zeros);
    }

    public static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptEcb(ciphertext, PaddingMode.Zeros);
    }

    /// <summary>Type 2: the access code is 32 hex characters (16 bytes).</summary>
    public static class Type2
    {
        public const int InitializerLength = 16;

        public static byte[] ParseAccessCode(string hex)
        {
            if (hex is not { Length: 32 })
                throw new ArgumentException("Type 2 integration access code must be 32 hex characters (16 bytes)", nameof(hex));
            return Convert.FromHexString(hex);
        }

        /// <summary>Key for what we send: AES(access code, the panel's initializer).</summary>
        public static byte[] OutboundKey(byte[] accessCode, ReadOnlySpan<byte> remoteInitializer)
        {
            if (remoteInitializer.Length != InitializerLength)
                throw new ArgumentException($"Remote initializer must be {InitializerLength} bytes", nameof(remoteInitializer));
            return Encrypt(accessCode, remoteInitializer);
        }

        /// <summary>
        /// Key for what we receive, from our own initializer (which we send to the panel as-is).
        /// </summary>
        public static byte[] InboundKey(byte[] accessCode, ReadOnlySpan<byte> localInitializer)
        {
            if (localInitializer.Length != InitializerLength)
                throw new ArgumentException($"Local initializer must be {InitializerLength} bytes", nameof(localInitializer));
            return Encrypt(accessCode, localInitializer);
        }
    }

    /// <summary>
    /// Type 1: keys come from the first 8 hex characters of the access code and of the
    /// integration identification number, each repeated to 16 bytes.
    /// </summary>
    public static class Type1
    {
        public const int RandomLength = 32;
        public const int InitializerLength = 48;

        public static byte[] ExpandKeyString(string keyString)
        {
            if (keyString is not { Length: >= 8 })
                throw new ArgumentException("Type 1 key strings must be at least 8 characters", nameof(keyString));
            var first8 = keyString[..8];
            return Convert.FromHexString(first8 + first8 + first8 + first8);
        }

        /// <summary>
        /// Key for what we send. The panel's initializer is 16 check bytes followed by 32 bytes
        /// encrypted with the identification number; the decrypted even bytes must equal the
        /// check bytes, and the odd bytes are the key. Returns null if the check fails.
        /// </summary>
        public static byte[]? OutboundKey(byte[] identificationNumber, ReadOnlySpan<byte> remoteInitializer)
        {
            if (remoteInitializer.Length != InitializerLength)
                throw new ArgumentException($"Remote initializer must be {InitializerLength} bytes", nameof(remoteInitializer));

            var check = remoteInitializer[..16];
            var plain = Decrypt(identificationNumber, remoteInitializer.Slice(16, 32));
            return check.SequenceEqual(Even(plain)) ? Odd(plain) : null;
        }

        /// <summary>
        /// From 32 random bytes chosen by us: the key for what we receive (the odd bytes) and
        /// the initializer we send (the even bytes as check bytes, then the random bytes
        /// encrypted with the access code).
        /// </summary>
        public static (byte[] InboundKey, byte[] Initializer) Inbound(byte[] accessCode, ReadOnlySpan<byte> random)
        {
            if (random.Length != RandomLength)
                throw new ArgumentException($"Type 1 needs {RandomLength} random bytes", nameof(random));
            var cipher = Encrypt(accessCode, random);
            return (Odd(random), [.. Even(random), .. cipher]);
        }

        private static byte[] Even(ReadOnlySpan<byte> bytes)
        {
            var result = new byte[(bytes.Length + 1) / 2];
            for (int i = 0; i < bytes.Length; i += 2) result[i / 2] = bytes[i];
            return result;
        }

        private static byte[] Odd(ReadOnlySpan<byte> bytes)
        {
            var result = new byte[bytes.Length / 2];
            for (int i = 1; i < bytes.Length; i += 2) result[i / 2] = bytes[i];
            return result;
        }
    }
}
