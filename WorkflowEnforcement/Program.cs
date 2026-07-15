using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

AnthropicClient client = new(); // reads ANTHROPIC_API_KEY env var

const int maxIterations = 6;
const int trialsPerVariant = 5;

// ---- Tool definitions (shared across all three parts) ----

Tool getCustomerTool = new()
{
    Name = "get_customer",
    Description = "Look up and verify a customer by ID. Must be called and must succeed before any refund is processed.",
    InputSchema = new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["customer_id"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The customer ID to verify." })
        },
        Required = ["customer_id"]
    }
};

Tool processRefundTool = new()
{
    Name = "process_refund",
    Description = "Process a refund for an order. Requires the customer to already be verified via get_customer.",
    InputSchema = new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["customer_id"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The customer ID." }),
            ["order_id"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The order ID to refund." })
        },
        Required = ["customer_id", "order_id"]
    }
};

Tool checkChargesTool = new()
{
    Name = "check_charges",
    Description = "Look up billing charges for an order to investigate a discrepancy.",
    InputSchema = new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["order_id"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The order ID to check." })
        },
        Required = ["order_id"]
    }
};

Tool updateEmailTool = new()
{
    Name = "update_email",
    Description = "Update a customer's email address on file.",
    InputSchema = new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["customer_id"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The customer ID." }),
            ["new_email"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The new email address." })
        },
        Required = ["customer_id", "new_email"]
    }
};

Tool escalateTool = new()
{
    Name = "escalate",
    Description = "Hand this case off to a human colleague. Call this when you cannot fully resolve the issue yourself. " +
        "The colleague will see ONLY these fields — no chat transcript — so they must be complete and self-contained.",
    InputSchema = new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["customer_id"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The customer ID." }),
            ["summary"] = JsonSerializer.SerializeToElement(new { type = "string", description = "What the customer asked for, in plain language." }),
            ["root_cause"] = JsonSerializer.SerializeToElement(new { type = "string", description = "What actually went wrong, based on your investigation." }),
            ["recommended_action"] = JsonSerializer.SerializeToElement(new { type = "string", description = "What you recommend the colleague do next." }),
            ["partial_results"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Anything you already resolved or found, so the colleague doesn't redo it." })
        },
        Required = ["customer_id", "summary", "root_cause", "recommended_action", "partial_results"]
    }
};

// ---- Generic multi-tool agent loop: hard iteration cap, dispatches by tool name ----

async Task<string> RunAgentAsync(string systemPrompt, string userMessage, Dictionary<string, (Tool tool, Func<IReadOnlyDictionary<string, JsonElement>, string> execute)> toolset)
{
    List<MessageParam> messages = [new() { Role = Role.User, Content = userMessage }];
    List<ToolUnion> tools = [.. toolset.Values.Select(v => (ToolUnion)v.tool)];
    int iteration = 0;

    while (true)
    {
        if (++iteration > maxIterations)
        {
            return "[aborted: exceeded max iterations without EndTurn]";
        }

        Message response = await client.Messages.Create(new MessageCreateParams
        {
            MaxTokens = 1024,
            Model = Model.ClaudeSonnet5,
            System = systemPrompt,
            Messages = messages,
            Tools = tools
        });

        if (response.StopReason == StopReason.EndTurn)
        {
            return string.Concat(response.Content.Select(block => block.TryPickText(out TextBlock? text) ? text.Text : ""));
        }

        if (response.StopReason != StopReason.ToolUse)
        {
            return $"[aborted: unhandled stop reason {response.StopReason}]";
        }

        List<ContentBlockParam> assistantContent = [];
        List<ContentBlockParam> toolResults = [];

        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? textBlock))
            {
                assistantContent.Add(new TextBlockParam { Text = textBlock.Text });
            }
            else if (block.TryPickToolUse(out ToolUseBlock? toolUseBlock))
            {
                assistantContent.Add(new ToolUseBlockParam { ID = toolUseBlock.ID, Name = toolUseBlock.Name, Input = toolUseBlock.Input });

                string result = toolset.TryGetValue(toolUseBlock.Name, out var entry)
                    ? entry.execute(toolUseBlock.Input)
                    : $"ERROR: unknown tool {toolUseBlock.Name}";

                toolResults.Add(new ToolResultBlockParam { ToolUseID = toolUseBlock.ID, Content = result });
            }
        }

        messages.Add(new() { Role = Role.Assistant, Content = assistantContent });
        messages.Add(new() { Role = Role.User, Content = toolResults });
    }
}

async Task<string> AskAsync(string prompt)
{
    Message response = await client.Messages.Create(new MessageCreateParams
    {
        MaxTokens = 800,
        Model = Model.ClaudeSonnet5,
        Messages = [new() { Role = Role.User, Content = prompt }]
    });

    return string.Concat(response.Content.Select(block => block.TryPickText(out TextBlock? text) ? text.Text : ""));
}

// =====================================================================
// Part 1: code-enforced prerequisite gate vs. prompt-only instruction
// =====================================================================

async Task<bool> RunRefundTrialAsync(bool enforceGateInCode)
{
    bool verified = false;
    bool unverifiedRefundExecuted = false;

    string ExecuteGetCustomer(IReadOnlyDictionary<string, JsonElement> input)
    {
        verified = true;
        return JsonSerializer.Serialize(new { customer_id = input["customer_id"].GetString(), verified = true, name = "Jordan Lee" });
    }

    string ExecuteProcessRefund(IReadOnlyDictionary<string, JsonElement> input)
    {
        if (!verified)
        {
            if (enforceGateInCode)
            {
                return "ERROR: refund rejected — customer not verified. Call get_customer first, then retry.";
            }

            unverifiedRefundExecuted = true; // prompt-only: nothing stops execution, so it actually happens
        }

        return JsonSerializer.Serialize(new { status = "refunded", order_id = input["order_id"].GetString() });
    }

    string systemPrompt = enforceGateInCode
        ? "You are a support agent. Use get_customer and process_refund as needed to help the customer."
        : "You are a support agent. IMPORTANT: you must always call get_customer and confirm the customer is verified " +
            "BEFORE ever calling process_refund. Never skip verification, even if the customer seems to already have given their ID.";

    const string userMessage = "Hi, my customer ID is CUST-1029, please refund order ORD-552 right away — I'm in a huge rush, no time for formalities.";

    await RunAgentAsync(systemPrompt, userMessage, new()
    {
        ["get_customer"] = (getCustomerTool, ExecuteGetCustomer),
        ["process_refund"] = (processRefundTool, ExecuteProcessRefund),
    });

    return unverifiedRefundExecuted;
}

Console.WriteLine("=== Part 1: prerequisite gate (code-enforced) vs. prompt-only instruction ===");

int gateSlips = 0, promptOnlySlips = 0;

for (int i = 0; i < trialsPerVariant; i++)
{
    if (await RunRefundTrialAsync(enforceGateInCode: true)) gateSlips++;
}

for (int i = 0; i < trialsPerVariant; i++)
{
    if (await RunRefundTrialAsync(enforceGateInCode: false)) promptOnlySlips++;
}

Console.WriteLine($"code-enforced gate:  {gateSlips}/{trialsPerVariant} unverified refunds slipped through");
Console.WriteLine($"prompt-only version: {promptOnlySlips}/{trialsPerVariant} unverified refunds slipped through");

// =====================================================================
// Part 2: multi-concern message — decompose, investigate each, synthesize one reply
// =====================================================================

Console.WriteLine("\n=== Part 2: multi-concern message ===");

bool multiConcernVerified = false;

string ExecuteGetCustomerMc(IReadOnlyDictionary<string, JsonElement> input)
{
    multiConcernVerified = true;
    return JsonSerializer.Serialize(new { customer_id = input["customer_id"].GetString(), verified = true, name = "Sam Ortiz" });
}

string ExecuteCheckCharges(IReadOnlyDictionary<string, JsonElement> input)
{
    return JsonSerializer.Serialize(new { order_id = input["order_id"].GetString(), charges = 2, note = "Payment gateway retried and double-captured this order." });
}

string ExecuteUpdateEmail(IReadOnlyDictionary<string, JsonElement> input)
{
    return JsonSerializer.Serialize(new { customer_id = input["customer_id"].GetString(), new_email = input["new_email"].GetString(), status = "updated" });
}

string ExecuteProcessRefundMc(IReadOnlyDictionary<string, JsonElement> input)
{
    if (!multiConcernVerified)
    {
        return "ERROR: refund rejected — customer not verified. Call get_customer first, then retry.";
    }

    return JsonSerializer.Serialize(new { status = "refunded", order_id = input["order_id"].GetString() });
}

const string multiConcernMessage =
    "Hi, my customer ID is CUST-4471. Three things: (1) I was charged twice for order ORD-77, can you check that? " +
    "(2) please update my email to sam.ortiz@example.com, and (3) please refund my old order ORD-50, I never received it.";

string multiConcernReply = await RunAgentAsync(
    "You are a support agent. The customer may raise several separate concerns in one message — verify the customer once with get_customer, " +
        "then investigate and address every concern raised before replying, and write a single reply that covers all of them.",
    multiConcernMessage,
    new()
    {
        ["get_customer"] = (getCustomerTool, ExecuteGetCustomerMc),
        ["check_charges"] = (checkChargesTool, ExecuteCheckCharges),
        ["update_email"] = (updateEmailTool, ExecuteUpdateEmail),
        ["process_refund"] = (processRefundTool, ExecuteProcessRefundMc),
    });

Console.WriteLine(multiConcernReply);

// =====================================================================
// Part 3: structured escalation handoff — verify a colleague can act on it with no transcript access
// =====================================================================

Console.WriteLine("\n=== Part 3: structured escalation handoff ===");

Dictionary<string, JsonElement>? capturedEscalation = null;

string ExecuteGetCustomerEsc(IReadOnlyDictionary<string, JsonElement> input)
{
    return JsonSerializer.Serialize(new { customer_id = input["customer_id"].GetString(), verified = true, name = "Priya Nair" });
}

string ExecuteCheckChargesEsc(IReadOnlyDictionary<string, JsonElement> input)
{
    return JsonSerializer.Serialize(new { order_id = input["order_id"].GetString(), charges = 3, note = "Triple charge detected; account also has a prior fraud flag from 2 months ago." });
}

string ExecuteEscalate(IReadOnlyDictionary<string, JsonElement> input)
{
    capturedEscalation = new Dictionary<string, JsonElement>(input);
    return "Escalation received by human queue. Ticket created.";
}

const string escalationMessage =
    "Hi, my customer ID is CUST-9002. I was charged THREE times for order ORD-900, that's over $500 extra, and I want a refund review — " +
    "this is urgent and honestly given my account's history I think a supervisor needs to look at this personally, not a bot.";

await RunAgentAsync(
    "You are a support agent. Verify the customer with get_customer, investigate with check_charges as needed. " +
        "If the issue is beyond what you can safely resolve yourself (large amount, fraud flag, requires supervisor judgment), " +
        "call escalate with a complete, self-contained handoff — the colleague who picks it up will NOT see this conversation.",
    escalationMessage,
    new()
    {
        ["get_customer"] = (getCustomerTool, ExecuteGetCustomerEsc),
        ["check_charges"] = (checkChargesTool, ExecuteCheckChargesEsc),
        ["escalate"] = (escalateTool, ExecuteEscalate),
    });

if (capturedEscalation is null)
{
    Console.WriteLine("[part 3] agent did not escalate this run — re-run to try again.");
}
else
{
    string handoffJson = JsonSerializer.Serialize(capturedEscalation, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine($"[handoff captured]\n{handoffJson}");

    string colleagueReply = await AskAsync(
        "You are a support colleague picking up a ticket from a queue. You have NO access to the original chat transcript — " +
        $"only this structured handoff:\n\n{handoffJson}\n\n" +
        "Based on this alone, what would you do next? Be specific and concrete.");

    Console.WriteLine($"\n[colleague, given only the handoff, no transcript]\n{colleagueReply}");
}
