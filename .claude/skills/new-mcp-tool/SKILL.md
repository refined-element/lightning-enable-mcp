---
name: new-mcp-tool
description: Scaffold a new MCP tool with models, service method, and DI registration
---

Create a new MCP tool following project conventions. Use $ARGUMENTS for the tool name and description.

## Steps

1. **Create tool class** in the appropriate tools directory
   - Static class with `[McpServerToolType]` attribute
   - Static method with `[McpServerTool(Name = "snake_case_name")]` attribute
   - Add `[Description("...")]` on EVERY parameter
   - Use flat parameters (no complex objects)
   - Nullable optional service injection for wallet services
   - Include `IsConfigured` check — return error result if required service not configured

2. **Add models** to the appropriate models file if needed
   - Request/response records

3. **Add service interface method** in the service interface if the tool needs business logic beyond direct API calls

4. **Implement service method** in the implementation class

5. **Update DI registration** in `Program.cs` if new services are needed

6. **Write tests** following existing test patterns

## Patterns to Follow

```csharp
[McpServerToolType]
public static class MyNewTool
{
    [McpServerTool(Name = "my_new_tool")]
    [Description("What this tool does")]
    public static async Task<string> Execute(
        [Description("Parameter description")] string requiredParam,
        [Description("Optional parameter")] string? optionalParam = null,
        IMyService? myService = null)
    {
        if (myService is null || !myService.IsConfigured)
            return "Error: MyService is not configured. Set MY_ENV_VAR environment variable.";

        // Implementation
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }
}
```

## Key Conventions
- Tool name: `snake_case` (e.g., `get_btc_price`, `check_wallet_balance`)
- Class name: PascalCase + "Tool" suffix
- Return JSON-serialized results for structured data
- Use `[Description]` on every parameter — MCP clients use these for documentation
- Wallet priority: LND > NWC > Strike > OpenNode
- L402 requires preimage — only works with LND, Strike, CoinOS NWC, CLINK NWC, Alby Hub NWC

## Free vs Paid
- 15 free tools (no license needed)
- 2 producer tools require Agentic Commerce subscription ($99/mo+): `create_l402_challenge`, `verify_l402_payment`
- If the new tool should be paid, add license check logic

Suggested follow-up: `/mcp-publish-prep` when ready to publish
