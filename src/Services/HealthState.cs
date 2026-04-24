using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Jellyflix.Services;

/// <summary>
/// Thread-safe singleton that tracks plugin health. Populated by
/// Bootstrapper; read by HealthController (which the config page polls).
/// </summary>
public class HealthState
{
    public enum Status { Starting, Ready, Degraded, Error }

    private readonly ConcurrentDictionary<string, CheckResult> _checks = new();
    private string? _version;
    private string? _errorMessage;
    private Status _status = Status.Starting;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public void AddCheck(string key, bool ok, string message)
    {
        _checks[key] = new CheckResult(ok, message);
    }

    public void MarkReady(string version)
    {
        _version = version;
        _status = _checks.Values.Any(c => !c.Ok) ? Status.Degraded : Status.Ready;
    }

    public void MarkError(string message)
    {
        _status = Status.Error;
        _errorMessage = message;
    }

    public object Snapshot() => new
    {
        status = _status.ToString(),
        version = _version,
        uptimeSeconds = (int)(DateTime.UtcNow - _startedAt).TotalSeconds,
        error = _errorMessage,
        checks = _checks
            .OrderBy(kv => kv.Key)
            .Select(kv => new { key = kv.Key, ok = kv.Value.Ok, message = kv.Value.Message })
    };

    private record CheckResult(bool Ok, string Message);
}
