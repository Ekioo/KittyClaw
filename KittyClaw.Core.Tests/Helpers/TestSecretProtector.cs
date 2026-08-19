using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Helpers;

internal sealed class TestSecretProtector : IProjectSecretProtector
{
    private const byte Mask = 0xA7;

    public const byte Id = 0x7E;

    public byte ProtectorId => Id;
    public string DisplayName => "test protector";

    public byte[] Protect(byte[] plaintext) => plaintext.Select(value => (byte)(value ^ Mask)).ToArray();
    public byte[] Unprotect(byte[] ciphertext) => ciphertext.Select(value => (byte)(value ^ Mask)).ToArray();
}

internal sealed class FailingSecretProtector : IProjectSecretProtector
{
    public byte ProtectorId => 0x7D;
    public string DisplayName => "failing protector";

    public byte[] Protect(byte[] plaintext) => throw new InvalidOperationException("protection unavailable");
    public byte[] Unprotect(byte[] ciphertext) => throw new InvalidOperationException("protection unavailable");
}
