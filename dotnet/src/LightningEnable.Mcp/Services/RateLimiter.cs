using System.Collections.Concurrent;

namespace LightningEnable.Mcp.Services;

/// <summary>
/// Simple in-memory rate limiter for MCP tools.
/// Uses a sliding window algorithm to limit requests per tool.
/// </summary>
public class RateLimiter : IRateLimiter
{
    private readonly ConcurrentDictionary<string, SlidingWindowState> _windows = new();
    private readonly int _maxRequestsPerMinute;
    private readonly TimeSpan _windowDuration;

    public RateLimiter()
    {
        // Default: 60 requests per minute per tool
        _maxRequestsPerMinute = GetEnvInt("MCP_RATE_LIMIT_PER_MINUTE", 60);
        _windowDuration = TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// Checks if a request is allowed for the given tool.
    /// </summary>
    /// <param name="toolName">Name of the tool being called.</param>
    /// <returns>True if the request is allowed, false if rate limited.</returns>
    public bool IsAllowed(string toolName)
    {
        var now = DateTime.UtcNow;
        var state = _windows.GetOrAdd(toolName, _ => new SlidingWindowState());

        lock (state.Lock)
        {
            // Remove expired timestamps
            while (state.Timestamps.Count > 0 &&
                   now - state.Timestamps.Peek() > _windowDuration)
            {
                state.Timestamps.Dequeue();
            }

            // Check if we're at the limit
            if (state.Timestamps.Count >= _maxRequestsPerMinute)
            {
                return false;
            }

            // Record this request
            state.Timestamps.Enqueue(now);
            return true;
        }
    }

    /// <summary>
    /// Gets the number of requests remaining in the current window.
    /// </summary>
    /// <param name="toolName">Name of the tool.</param>
    /// <returns>Number of requests remaining, or -1 if unlimited.</returns>
    public int GetRemainingRequests(string toolName)
    {
        if (!_windows.TryGetValue(toolName, out var state))
        {
            return _maxRequestsPerMinute;
        }

        var now = DateTime.UtcNow;
        lock (state.Lock)
        {
            // Count non-expired timestamps
            var count = state.Timestamps.Count(t => now - t <= _windowDuration);
            return Math.Max(0, _maxRequestsPerMinute - count);
        }
    }

    /// <summary>
    /// Resets the rate limit state for a tool.
    /// </summary>
    /// <param name="toolName">Name of the tool to reset.</param>
    public void Reset(string toolName)
    {
        _windows.TryRemove(toolName, out _);
    }

    /// <summary>
    /// Resets all rate limit state.
    /// </summary>
    public void ResetAll()
    {
        _windows.Clear();
    }

    private static int GetEnvInt(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var result) ? result : defaultValue;
    }

    private class SlidingWindowState
    {
        public readonly object Lock = new();
        public readonly Queue<DateTime> Timestamps = new();
    }
}

/// <summary>
/// Interface for rate limiting MCP tool calls.
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Checks if a request is allowed for the given tool.
    /// </summary>
    bool IsAllowed(string toolName);

    /// <summary>
    /// Gets the number of requests remaining in the current window.
    /// </summary>
    int GetRemainingRequests(string toolName);

    /// <summary>
    /// Resets the rate limit state for a tool.
    /// </summary>
    void Reset(string toolName);

    /// <summary>
    /// Resets all rate limit state.
    /// </summary>
    void ResetAll();
}
