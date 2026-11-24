namespace Pixela.Application.Abstractions;

/// <summary>Cifrado de secretos en reposo (llaves de Claude, apps y tokens de Meta, llaves de integraciones, 2FA).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);

    /// <summary>
    /// Llave de 32 bytes derivada de la maestra para un uso concreto (p. ej. firmar las sesiones del chat). Así no hace falta otra variable
    /// de entorno por cada firma: cambia solo si cambia la llave maestra, y un propósito distinto nunca da la misma llave.
    /// </summary>
    byte[] DeriveKey(string purpose);
}
