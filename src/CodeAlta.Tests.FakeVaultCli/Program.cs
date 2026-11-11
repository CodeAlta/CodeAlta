using System.Text.Json;
using System.Text.Json.Nodes;

// A fake command line of a vault for the tests of the credential providers. It plays the program it is named after (op), or
// generic commands for the command provider, from a state file beside it (fake-vault.json), and logs each call (fake-vault.log).
var folder = AppContext.BaseDirectory;
var statePath = Path.Combine(folder, "fake-vault.json");
var state = File.Exists(statePath) ? JsonNode.Parse(File.ReadAllText(statePath))!.AsObject() : new JsonObject();
File.AppendAllText(Path.Combine(folder, "fake-vault.log"), string.Join(' ', args) + Environment.NewLine);
var program = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "op").ToLowerInvariant();
if (Environment.GetEnvironmentVariable("FAKE_VAULT_REQUIRE_ENV") is { Length: > 0 } required && Environment.GetEnvironmentVariable(required) is null)
{
    Console.Error.WriteLine($"missing {required}");
    return 3;
}

return args switch
{
    ["print", var text] => Print(text),
    ["print-json", var json] => Print(json),
    ["fail", var message] => Fail(message, 2),
    ["status-ok"] => 0,
    ["status-locked"] => Fail("the vault is locked", 1),
    ["env", var name] => Print(Environment.GetEnvironmentVariable(name) ?? "(unset)"),
    ["--version"] => Print("2.42.0"),
    _ when program == "op" => Op(args),
    _ => Fail("unknown command", 64),
};

int Op(string[] arguments)
{
    var list = arguments.ToList();
    var account = TakeOption(list, "--account");
    if (state["serviceAccount"]?.GetValue<bool>() == true && Environment.GetEnvironmentVariable("OP_SERVICE_ACCOUNT_TOKEN") is not { Length: > 0 })
        return Fail("[ERROR] no account found for filter", 1);
    if (state["accounts"]?.GetValue<bool>() == false && Environment.GetEnvironmentVariable("OP_SERVICE_ACCOUNT_TOKEN") is not { Length: > 0 })
        return Fail("No accounts configured for use with 1Password CLI.", 1);
    var signedIn = state["signedIn"]?.GetValue<bool>() == true || Environment.GetEnvironmentVariable("OP_SERVICE_ACCOUNT_TOKEN") is { Length: > 0 };
    switch (list)
    {
        case ["whoami", ..]:
            return signedIn ? Print("{\"url\":\"my.1password.com\",\"email\":\"alice@example.com\"}") : Fail("[ERROR] account is not signed in", 1);
        case ["signin", ..]:
        case ["vault", "list", ..]:
        case ["read", ..]:
        case ["item", ..]:
            if (!signedIn)
            {
                // The prompt of the app: the state says whether the user approves it.
                if (state["approve"]?.GetValue<bool>() != true) return Fail("[ERROR] authorization prompt dismissed, please try again", 1);
                state["signedIn"] = true;
                File.WriteAllText(statePath, state.ToJsonString());
            }

            break;
    }

    var vaults = state["vaults"]?.AsArray() ?? [];
    switch (list)
    {
        case ["signin", ..]:
            return 0;
        case ["vault", "list", ..]:
            return Print(new JsonArray([.. vaults.Select(vault => (JsonNode)new JsonObject { ["id"] = vault!["id"]!.GetValue<string>(), ["name"] = vault["name"]!.GetValue<string>() })]).ToJsonString());
        case ["item", "list", ..]:
        {
            var vault = Vault(TakeOption(list, "--vault"));
            if (vault is null) return Fail("[ERROR] isn't a vault in this account", 1);
            return Print(new JsonArray([.. vault["items"]!.AsArray().Select(item => (JsonNode)new JsonObject { ["id"] = item!["id"]!.GetValue<string>(), ["title"] = item["title"]!.GetValue<string>(), ["category"] = item["category"]?.GetValue<string>() ?? "LOGIN" })]).ToJsonString());
        }
        case ["item", "get", var itemId, ..]:
        {
            var vault = Vault(TakeOption(list, "--vault"));
            var item = vault?["items"]!.AsArray().FirstOrDefault(entry => Same(entry!["id"], itemId) || Same(entry["title"], itemId));
            if (vault is null || item is null) return Fail("[ERROR] isn't an item", 1);
            var fields = new JsonArray();
            foreach (var field in item["fields"]!.AsArray())
            {
                fields.Add(new JsonObject
                {
                    ["id"] = field!["id"]!.GetValue<string>(), ["label"] = field["label"]?.GetValue<string>(), ["type"] = field["type"]?.GetValue<string>() ?? "STRING",
                    ["value"] = field["value"]?.GetValue<string>(),
                    ["reference"] = $"op://{vault["name"]}/{item["title"]}/{field["id"]}",
                });
            }

            return Print(new JsonObject { ["id"] = item["id"]!.GetValue<string>(), ["title"] = item["title"]!.GetValue<string>(), ["vault"] = new JsonObject { ["id"] = vault["id"]!.GetValue<string>(), ["name"] = vault["name"]!.GetValue<string>() }, ["fields"] = fields }.ToJsonString());
        }
        case ["read", .., var reference]:
        {
            var path = reference["op://".Length..];
            var query = path.IndexOf('?');
            var otp = query >= 0 && path[query..].Contains("attribute=otp");
            if (query >= 0) path = path[..query];
            var parts = path.Split('/');
            if (parts.Length < 3) return Fail("[ERROR] invalid secret reference", 1);
            var vault = Vault(parts[0]);
            var item = vault?["items"]!.AsArray().FirstOrDefault(entry => Same(entry!["id"], parts[1]) || Same(entry["title"], parts[1]));
            var field = item?["fields"]!.AsArray().FirstOrDefault(entry => Same(entry!["id"], parts[^1]) || Same(entry["label"], parts[^1]));
            if (field is null) return Fail($"[ERROR] could not read secret '{reference}': isn't an item or does not have a field", 1);
            Console.Out.Write(otp ? "123456" : field["value"]!.GetValue<string>());
            return 0;
        }
        default:
            return Fail("[ERROR] unknown command", 1);
    }
}

JsonNode? Vault(string? name) => (state["vaults"]?.AsArray() ?? []).FirstOrDefault(vault => Same(vault!["id"], name) || Same(vault["name"], name));

static bool Same(JsonNode? node, string? text) => node is not null && text is not null && string.Equals(node.GetValue<string>(), text, StringComparison.OrdinalIgnoreCase);

static string? TakeOption(List<string> list, string option)
{
    var at = list.IndexOf(option);
    if (at < 0 || at + 1 >= list.Count) return null;
    var value = list[at + 1];
    list.RemoveRange(at, 2);
    return value;
}

static int Print(string text)
{
    Console.Out.WriteLine(text);
    return 0;
}

static int Fail(string message, int code)
{
    Console.Error.WriteLine(message);
    return code;
}
