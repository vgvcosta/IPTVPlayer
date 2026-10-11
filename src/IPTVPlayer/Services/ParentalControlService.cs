using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using IPTVPlayer.Services.Data;

namespace IPTVPlayer.Services;

/// <summary>Onde está o grupo bloqueado: canais, categorias de filmes ou de séries.</summary>
public enum LockScope
{
    Live = 0,
    Movies = 1,
    Series = 2,
}

/// <summary>
/// Controlo parental: um PIN (guardado só como hash PBKDF2-SHA256 com sal, nunca o PIN) e uma lista
/// de grupos bloqueados por playlist. Com PIN definido e a sessão bloqueada, o conteúdo desses grupos
/// fica escondido da lista, da pesquisa, dos favoritos e do guia até se introduzir o PIN.
/// </summary>
public sealed partial class ParentalControlService
{
    private const string PinSetting = "parental_pin";
    private const int Iterations = 100_000;

    private readonly SettingsRepository _settings;
    private readonly AppDatabase _db;
    private string? _pinRecord;
    private bool _loaded;

    public ParentalControlService(SettingsRepository settings, AppDatabase db)
    {
        _settings = settings;
        _db = db;
    }

    /// <summary>Bloqueio/desbloqueio ou mudança de PIN/grupos (para voltar a filtrar as listas).</summary>
    public event EventHandler? Changed;

    public bool HasPin
    {
        get
        {
            EnsureLoaded();
            return _pinRecord is not null;
        }
    }

    /// <summary>A sessão foi desbloqueada com o PIN (volta a bloquear ao fechar a app ou com "Bloquear").</summary>
    public bool IsUnlocked { get; private set; }

    /// <summary>O conteúdo bloqueado deve estar escondido agora.</summary>
    public bool IsActive => HasPin && !IsUnlocked;

    public static bool IsValidPin(string? pin) => pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit);

    public bool Verify(string? pin)
    {
        EnsureLoaded();
        if (_pinRecord is null || pin is null) return false;
        var parts = _pinRecord.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public bool Unlock(string? pin)
    {
        if (!Verify(pin)) return false;
        IsUnlocked = true;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Lock()
    {
        if (!IsUnlocked) return;
        IsUnlocked = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Define ou altera o PIN (4 a 8 algarismos). A sessão fica desbloqueada.</summary>
    public void SetPin(string pin)
    {
        if (!IsValidPin(pin)) throw new UserFacingException("O PIN tem de ter entre 4 e 8 algarismos.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32);
        _pinRecord = string.Join('$', "pbkdf2", Iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt), Convert.ToBase64String(hash));
        _settings.Set(PinSetting, _pinRecord);
        IsUnlocked = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemovePin()
    {
        _pinRecord = null;
        _settings.Set(PinSetting, null);
        IsUnlocked = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public HashSet<string> GetLockedGroups(long playlistId, LockScope scope)
    {
        using var c = _db.Open();
        return c.Query<string>("SELECT name FROM locked_groups WHERE playlist_id = @playlistId AND scope = @scope",
            new { playlistId, scope = (int)scope }).ToHashSet(StringComparer.Ordinal);
    }

    public void SetLockedGroups(long playlistId, LockScope scope, IEnumerable<string> names)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM locked_groups WHERE playlist_id = @playlistId AND scope = @scope",
            new { playlistId, scope = (int)scope }, tx);
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            c.Execute("INSERT INTO locked_groups (playlist_id, scope, name) VALUES (@playlistId, @scope, @name)",
                new { playlistId, scope = (int)scope, name }, tx);
        }
        tx.Commit();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sugestão automática de grupos para adultos ("Adultos", "XXX", "+18"…).</summary>
    public static bool LooksAdult(string groupName) => AdultPattern().IsMatch(TextNormalizer.ForSearch(groupName));

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _pinRecord = _settings.Get(PinSetting);
        _loaded = true;
    }

    // Só termos inequívocos: "sex…" apanharia "Sexta-feira".
    [GeneratedRegex(@"(^|[^a-z])(adult\w*|xxx|porn\w*|erotic\w*|hentai|18\s*\+|\+\s*18)([^a-z]|$)")]
    private static partial Regex AdultPattern();
}
