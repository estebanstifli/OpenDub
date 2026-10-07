namespace Dubbing.Core;

public class EngineException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class UnsupportedCapabilityException(string message) : EngineException(message);
public sealed class ModelNotInstalledException(string message) : EngineException(message);
public sealed class RuntimeCrashedException(string message) : EngineException(message);
public sealed class MediaProcessingException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class ProjectFormatException(string message, Exception? inner = null) : Exception(message, inner);
