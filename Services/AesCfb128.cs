using System;
using System.Security.Cryptography;

namespace Mosytool.Services;

/// <summary>
/// AES-CFB128（等价于 PyCryptodome 的 AES.MODE_CFB + segment_size=128）。
/// .NET 的 Aes 不直接支持 CFB，所以基于 ECB 原语自己实现反馈。
/// </summary>
public sealed class AesCfb128 : IDisposable
{
    private readonly ICryptoTransform _encryptor;
    private readonly byte[] _feedback = new byte[16];
    private readonly byte[] _keystream = new byte[16];
    private bool _disposed;

    public AesCfb128(byte[] key, byte[] iv)
    {
        if (iv.Length < 16) throw new ArgumentException("IV 至少 16 字节。");

        var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        _encryptor = aes.CreateEncryptor();

        Array.Copy(iv, _feedback, 16);
    }

    /// <summary>解密（CFB128 解密与加密互为同一反馈结构，这里只实现解密）。</summary>
    public byte[] Decrypt(byte[] data) => Decrypt(data, 0, data.Length);

    public byte[] Decrypt(byte[] data, int offset, int count)
    {
        var output = new byte[count];
        int pos = offset;
        int outPos = 0;
        int remaining = count;

        while (remaining >= 16)
        {
            _encryptor.TransformBlock(_feedback, 0, 16, _keystream, 0);
            for (int i = 0; i < 16; i++) output[outPos + i] = (byte)(data[pos + i] ^ _keystream[i]);
            Buffer.BlockCopy(data, pos, _feedback, 0, 16);
            pos += 16;
            outPos += 16;
            remaining -= 16;
        }

        if (remaining > 0)
        {
            _encryptor.TransformBlock(_feedback, 0, 16, _keystream, 0);
            for (int i = 0; i < remaining; i++) output[outPos + i] = (byte)(data[pos + i] ^ _keystream[i]);
        }

        return output;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encryptor.Dispose();
    }
}
