namespace VpnControl.Requests;

/// Result status codes of the OnForkHub/MinimalTemplate request-result
/// pattern: every use case reports outcome through these, and BaseEndPoint
/// maps them to HTTP status codes.
public enum EResultStatus
{
    Success,
    HasValidation,
    HasError,
    EntityNotFound,
    EntityHasError,
    EntityAlreadyExists,
    NoContent
}
