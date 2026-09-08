using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace PiStation.Host.Hosting;

/// <summary>Limits unauthenticated pairing without charging ordinary authenticated traffic.</summary>
public static class RemotePairingRateLimiter
{
    public static PartitionedRateLimiter<HttpContext> Create(int perAddressLimit = 120, int globalLimit = 1200)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perAddressLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(globalLimit);
        // Check the address budget first, so rejected traffic cannot exhaust the shared budget.
        return PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context => IsPairing(context)
                ? RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown", _ => Window(perAddressLimit))
                : RateLimitPartition.GetNoLimiter("other")),
            PartitionedRateLimiter.Create<HttpContext, string>(context => IsPairing(context)
                ? RateLimitPartition.GetFixedWindowLimiter("pairing", _ => Window(globalLimit))
                : RateLimitPartition.GetNoLimiter("other")));
    }

    private static bool IsPairing(HttpContext context) =>
        context.Request.Path == "/remote/pair" || context.Request.Path == "/remote/pair/status";

    private static FixedWindowRateLimiterOptions Window(int limit) => new()
    { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true };
}
