namespace PinCC.Core;

/// <summary>An input exceeded a bounded extraction budget; callers may offer the existing file/web grant fallback.</summary>
public sealed class SourceLimitException(string message) : IOException(message);
