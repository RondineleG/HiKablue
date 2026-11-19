namespace VpnControl.Requests;

/// One validation failure: which property failed and why.
public record RequestValidation(string PropertyName, string Description);
