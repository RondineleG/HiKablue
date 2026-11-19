namespace VpnControl.Requests;

/// Result pattern adapted from OnForkHub.Core (via MinimalTemplate): a
/// request never throws for a business outcome — it returns a status plus
/// optional errors, and the endpoint maps that to an HTTP response.
public class RequestResult
{
    private readonly List<string> _validationErrors = new();

    protected RequestResult(EResultStatus status, bool isSuccess, string message, DateTime date)
    {
        Status = status;
        IsSuccess = isSuccess;
        Message = message;
        Date = date;
    }

    public RequestResult()
    {
        Status = EResultStatus.Success;
        IsSuccess = true;
        Message = string.Empty;
        Date = DateTime.UtcNow;
    }

    public EResultStatus Status { get; protected set; }
    public bool IsSuccess { get; protected set; }
    public string Message { get; protected set; }
    public DateTime Date { get; protected set; }
    public string? ErrorMessage { get; protected set; }
    public IReadOnlyList<string> ValidationErrors => _validationErrors;

    public static RequestResult Success(string? message = null) =>
        new(EResultStatus.Success, true, message ?? "Success", DateTime.UtcNow);

    public static RequestResult WithError(string message) =>
        new(EResultStatus.HasError, false, message, DateTime.UtcNow)
        {
            ErrorMessage = message
        };

    public static RequestResult NotFound(string message) =>
        new(EResultStatus.EntityNotFound, false, message, DateTime.UtcNow)
        {
            ErrorMessage = message
        };

    public static RequestResult NoContent(string? message = null) =>
        new(EResultStatus.NoContent, true, message ?? "No content", DateTime.UtcNow);

    protected void AddValidationErrors(IEnumerable<string> errors) =>
        _validationErrors.AddRange(errors);
}

public class RequestResult<T> : RequestResult
{
    public RequestResult()
    {
    }

    protected RequestResult(EResultStatus status, bool isSuccess, string message, DateTime date, T? data)
        : base(status, isSuccess, message, date)
    {
        Data = data;
    }

    public T? Data { get; }

    public static RequestResult<T> Success(T data, string? message = null) =>
        new(EResultStatus.Success, true, message ?? "Success", DateTime.UtcNow, data);

    public static new RequestResult<T> WithError(string message) =>
        new(EResultStatus.HasError, false, message, DateTime.UtcNow, default)
        {
            ErrorMessage = message
        };

    public static RequestResult<T> WithError(string message, EResultStatus status) =>
        new(status, false, message, DateTime.UtcNow, default)
        {
            ErrorMessage = message
        };

    public static new RequestResult<T> NotFound(string message) =>
        new(EResultStatus.EntityNotFound, false, message, DateTime.UtcNow, default)
        {
            ErrorMessage = message
        };

    public static RequestResult<T> WithValidations(params RequestValidation[] validations)
    {
        var message = string.Join("; ", validations.Select(v => string.IsNullOrWhiteSpace(v.PropertyName)
            ? v.Description
            : $"{v.PropertyName}: {v.Description}"));
        var result = new RequestResult<T>(EResultStatus.HasValidation, false, message, DateTime.UtcNow, default)
        {
            ErrorMessage = message
        };
        result.AddValidationErrors(validations.Select(v => string.IsNullOrWhiteSpace(v.PropertyName)
            ? v.Description
            : $"{v.PropertyName}: {v.Description}"));
        return result;
    }
}
