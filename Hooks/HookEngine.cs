using System.Globalization;
using System.Text.Json.Nodes;

namespace Hooks;

// ---------------------------------------------------------------------
// Part 1: PostToolUse normalization
//
// Three finance/support tools each report "when + what state" using a
// different wire format. The hook's job is to fold all three into one
// shape before anything downstream (the model, a dashboard, logging)
// has to deal with them.
// ---------------------------------------------------------------------

public sealed record NormalizedStatus(string Tool, string Status, string TimestampUtc, string SourceFormat);

public static class StatusNormalizer
{
    private static readonly Dictionary<int, string> StatusCodeLabels = new()
    {
        [1] = "open",
        [2] = "pending",
        [3] = "resolved",
    };

    // PostToolUse hook entry point: tool_name + tool_response -> normalized shape.
    public static NormalizedStatus Normalize(string toolName, JsonNode raw)
    {
        if (raw["status_ts"] is JsonValue unixVal && unixVal.TryGetValue(out long unixSeconds))
        {
            var ts = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            return new NormalizedStatus(toolName, "unknown", ts.UtcDateTime.ToString("O"), "unix");
        }

        if (raw["status_ts"] is JsonValue isoVal && isoVal.TryGetValue(out string? isoString))
        {
            var ts = DateTimeOffset.Parse(isoString!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
            return new NormalizedStatus(toolName, "unknown", ts.UtcDateTime.ToString("O"), "iso8601");
        }

        if (raw["status_code"] is JsonValue codeVal && codeVal.TryGetValue(out int code))
        {
            var label = StatusCodeLabels.GetValueOrDefault(code, $"code_{code}");
            // This tool never reports a time, only a state code — the hook stamps
            // processing time so the shape is still uniform, not because it's "when it happened".
            return new NormalizedStatus(toolName, label, DateTime.UtcNow.ToString("O"), "status_code");
        }

        throw new InvalidOperationException($"Tool '{toolName}' returned an unrecognized shape: {raw}");
    }
}

// ---------------------------------------------------------------------
// Part 2: PreToolUse refund guard
//
// Mirrors Claude Code's real hook output contract:
//   { "hookSpecificOutput": { "hookEventName": "PreToolUse",
//                             "permissionDecision": "deny"|"allow",
//                             "permissionDecisionReason": "..." } }
// ---------------------------------------------------------------------

public sealed record PreToolUseDecision(string PermissionDecision, string? Reason, string? EscalationTicket = null);

public static class RefundGuard
{
    public const decimal EscalationThreshold = 500m;
    private static int _escalationCounter = 0;

    public static PreToolUseDecision Evaluate(string toolName, JsonNode input)
    {
        if (toolName != "process_refund")
        {
            return new PreToolUseDecision("allow", null);
        }

        var amount = input["amount_usd"]!.GetValue<decimal>();
        if (amount > EscalationThreshold)
        {
            var ticket = $"ESC-{++_escalationCounter:000}";
            return new PreToolUseDecision(
                "deny",
                $"Refund of ${amount:F2} exceeds the ${EscalationThreshold:F2} auto-approval threshold; " +
                $"redirected to senior support escalation queue (ticket {ticket}).",
                ticket);
        }

        return new PreToolUseDecision("allow", null);
    }
}

// ---------------------------------------------------------------------
// Mock finance tool with a real, observable side effect (a ledger debit),
// so Part 3 can prove that a PostToolUse-only guard is already too late.
// ---------------------------------------------------------------------

public sealed class RefundLedger
{
    public decimal Balance { get; private set; } = 10_000m;

    public string ProcessRefund(string customerId, decimal amount)
    {
        Balance -= amount;
        return $"Refunded ${amount:F2} to {customerId}. New company balance: ${Balance:F2}.";
    }
}

// ---------------------------------------------------------------------
// Part 4: exit-code semantics for hooks that speak plain exit codes
// instead of the JSON permissionDecision contract.
//   exit 0 -> success, action proceeds
//   exit 2 -> blocking error, action is blocked, stderr fed back to the agent
//   any other non-zero (e.g. 1) -> non-blocking error, action still proceeds,
//                                    stderr just shown to the user
// ---------------------------------------------------------------------

public static class HookExitCode
{
    public static bool ActionProceeds(int exitCode) => exitCode != 2;
}
