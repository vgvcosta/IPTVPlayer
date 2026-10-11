namespace IPTVPlayer.Services;

/// <summary>Erro esperado cuja mensagem pode ser mostrada diretamente ao utilizador.</summary>
public class UserFacingException : Exception
{
    public UserFacingException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
