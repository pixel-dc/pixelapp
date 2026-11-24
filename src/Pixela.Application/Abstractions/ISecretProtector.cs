namespace Pixela.Application.Abstractions;

/// <summary>Cifrado de secretos en reposo (llaves de Claude, apps y tokens de Meta, llaves de integraciones, 2FA).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}
