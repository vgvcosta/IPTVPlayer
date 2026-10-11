using System.Security.Cryptography;
using System.Text;

namespace IPTVPlayer.Services.Security;

/// <summary>
/// Encriptação rápida (AES-256-GCM) dos URLs de stream guardados em cache: muitos fornecedores
/// incluem utilizador/password no próprio URL de cada canal. DPAPI por linha seria lento para
/// 20 000+ canais, por isso a chave AES é gerada uma vez e guardada protegida com DPAPI.
/// Formato: nonce (12) | tag (16) | texto cifrado.
/// </summary>
public sealed class StreamUrlCipher : IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly AesGcm _aes;
    private readonly object _sync = new();

    public StreamUrlCipher(AppPaths paths, CredentialProtector protector)
    {
        var key = LoadOrCreateKey(paths.KeyPath, protector, out var reset);
        KeyWasReset = reset;
        _aes = new AesGcm(key, TagSize);
        CryptographicOperations.ZeroMemory(key);
    }

    /// <summary>
    /// A chave existente não pôde ser lida (ex.: perfil copiado de outro PC) e foi criada uma nova.
    /// Os canais em cache deixam de ser legíveis e devem ser apagados.
    /// </summary>
    public bool KeyWasReset { get; }

    public byte[] Seal(string text)
    {
        var plain = Encoding.UTF8.GetBytes(text);
        var output = new byte[NonceSize + TagSize + plain.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        lock (_sync)
        {
            _aes.Encrypt(nonce, plain, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize));
        }
        return output;
    }

    public string Open(byte[] data)
    {
        if (data.Length < NonceSize + TagSize)
            throw new UserFacingException("Os dados deste canal estão corrompidos. Atualiza a playlist.");

        var plain = new byte[data.Length - NonceSize - TagSize];
        try
        {
            lock (_sync)
            {
                _aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize),
                    data.AsSpan(NonceSize, TagSize), plain);
            }
        }
        catch (CryptographicException ex)
        {
            throw new UserFacingException("Não foi possível ler os dados deste canal. Atualiza a playlist.", ex);
        }
        return Encoding.UTF8.GetString(plain);
    }

    public void Dispose() => _aes.Dispose();

    private static byte[] LoadOrCreateKey(string path, CredentialProtector protector, out bool reset)
    {
        reset = false;
        if (File.Exists(path))
        {
            try
            {
                var key = protector.UnprotectBytes(File.ReadAllBytes(path));
                if (key.Length == 32) return key;
            }
            catch (Exception ex)
            {
                Log.Warn("Chave de encriptação ilegível; vai ser criada uma nova.", ex);
            }
            reset = true;
        }

        var newKey = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, protector.ProtectBytes(newKey));
        return newKey;
    }
}
