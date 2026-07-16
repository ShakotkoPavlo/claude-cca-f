using System.Text.Json.Nodes;
using Hooks;

Section("PART 1 — PostToolUse: normalize three timestamp shapes into one");
{
    var shipment = JsonNode.Parse("""{"tracking_id":"SHP-1","status_ts":1786958400}""")!;
    var ticket = JsonNode.Parse("""{"ticket_id":"TCK-2","status_ts":"2026-07-16T09:15:00Z"}""")!;
    var account = JsonNode.Parse("""{"account_id":"ACC-3","status_code":3}""")!;

    foreach (var (tool, raw) in new[] { ("get_shipment_status", shipment), ("get_ticket_status", ticket), ("get_account_status", account) })
    {
        var normalized = StatusNormalizer.Normalize(tool, raw);
        Console.WriteLine($"  raw ({normalized.SourceFormat,-11}) : {raw}");
        Console.WriteLine($"  normalized       : tool={normalized.Tool}, status={normalized.Status}, timestamp_utc={normalized.TimestampUtc}");
        Console.WriteLine();
    }
}

Section("PART 2 — PreToolUse: block process_refund over $500, redirect to escalation");
{
    var smallRefund = JsonNode.Parse("""{"customer_id":"CUST-1","amount_usd":120}""")!;
    var bigRefund = JsonNode.Parse("""{"customer_id":"CUST-2","amount_usd":750}""")!;

    foreach (var input in new[] { smallRefund, bigRefund })
    {
        var decision = RefundGuard.Evaluate("process_refund", input);
        Console.WriteLine($"  tool_input: {input}");
        Console.WriteLine($"  hookSpecificOutput.permissionDecision: {decision.PermissionDecision}");
        if (decision.Reason is not null)
            Console.WriteLine($"  hookSpecificOutput.permissionDecisionReason: {decision.Reason}");
        Console.WriteLine($"  tool executes: {(decision.PermissionDecision == "deny" ? "NO — blocked before it ever runs" : "yes")}");
        Console.WriteLine();
    }
}

Section("PART 3 — Same logic in PostToolUse instead: the refund already went through");
{
    var ledger = new RefundLedger();
    var input = JsonNode.Parse("""{"customer_id":"CUST-2","amount_usd":750}""")!;
    var customerId = input["customer_id"]!.GetValue<string>();
    var amount = input["amount_usd"]!.GetValue<decimal>();

    Console.WriteLine($"  balance before tool call : ${ledger.Balance:F2}");

    // Tool runs to completion — this is what "PostToolUse" means: the hook only
    // sees the world AFTER this line has already executed.
    var toolResult = ledger.ProcessRefund(customerId, amount);
    Console.WriteLine($"  tool executed            : {toolResult}");
    Console.WriteLine($"  balance after tool call  : ${ledger.Balance:F2}  <-- money is already gone");

    // Now the guard runs, as a PostToolUse hook would.
    var decision = RefundGuard.Evaluate("process_refund", input);
    Console.WriteLine($"  PostToolUse hook fires now, decides: {decision.PermissionDecision}");
    Console.WriteLine($"  reason: {decision.Reason}");
    Console.WriteLine("  outcome: hook can still deny showing the result to the model or feed an error back,");
    Console.WriteLine("           but it cannot undo the $750 debit. Direction matters: PreToolUse gates the");
    Console.WriteLine("           action; PostToolUse only reacts after the fact.");
}

Section("PART 4 — Exit-code rule: exit 1 proceeds, exit 2 blocks");
{
    foreach (var exitCode in new[] { 0, 1, 2 })
    {
        var proceeds = HookExitCode.ActionProceeds(exitCode);
        var meaning = exitCode switch
        {
            0 => "success",
            2 => "blocking error (stderr fed back to the agent)",
            _ => "non-blocking error (stderr shown to the user only)",
        };
        Console.WriteLine($"  exit {exitCode} -> {meaning,-45} -> action proceeds: {proceeds}");
    }
}

static void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', title.Length));
    Console.WriteLine(title);
    Console.WriteLine(new string('=', title.Length));
}
