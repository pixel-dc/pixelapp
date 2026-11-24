namespace Pixela.Application.Common;

public sealed record ServiceError(string Code, string Message, IReadOnlyDictionary<string, string>? Fields = null);

/// <summary>Resultado de un caso de uso: valor o error de negocio (sin excepciones para flujos esperados).</summary>
public sealed class ServiceResult<T>
{
    private ServiceResult(T? value, ServiceError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public ServiceError? Error { get; }
    public bool IsSuccess => Error is null;

    public static ServiceResult<T> Ok(T value) => new(value, null);

    public static ServiceResult<T> Fail(string code, string message, IReadOnlyDictionary<string, string>? fields = null) =>
        new(default, new ServiceError(code, message, fields));
}
