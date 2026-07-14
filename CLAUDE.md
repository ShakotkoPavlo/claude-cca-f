# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

`AgenticLoops` — a single-file .NET 8 console app prototyping the Anthropic C# SDK's tool-use (agentic) loop: send a message, check `StopReason`, execute a tool on `ToolUse`, loop until `EndTurn`. Currently a work-in-progress scratch project (`Program.cs` does not compile as-is).

## Commands

```bash
dotnet build          # build
dotnet run             # run Program.cs
```

No test project, no lint config, and no CI in this repo yet.

## Architecture

- Single top-level-statements file (`Program.cs`) — no layering to speak of yet.
- Uses `Anthropic.Models.Beta.Sessions.Tool` (aliased as `Tool`) and `Anthropic.Models.Messages.MessageCreateParams` from the `Anthropic` NuGet package (v12.35.1), targeting `Model.ClaudeSonnet5`.
- Intended shape: build `MessageCreateParams` with a `Tools` array → call `client.Messages.Create` in a loop → on `StopReason.ToolUse`, execute the matching tool and feed a `tool_result` back in → on `StopReason.EndTurn`, print `message.Content` and stop. This loop is not yet finished.

## Security note

Never hardcode `AuthToken` / API keys in `Program.cs`. Use `ANTHROPIC_API_KEY` env var, or `ant auth login` — a zero-arg `new AnthropicClient()` picks up either automatically.
