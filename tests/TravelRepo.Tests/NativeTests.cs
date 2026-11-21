using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TravelRepo.Git;
using TravelRepo.Native;
using Xunit;
namespace TravelRepo.Tests;

/// <summary>The C ABI in TravelRepo.Native, called through its exported functions as a native client would.</summary>
public sealed class NativeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "travelrepo-native-" + Guid.NewGuid().ToString("N"));
    private static readonly ConcurrentDictionary<nint, TaskCompletionSource<string>> Answers = new();
    private static long lastContext;

    public void Dispose() => TestFiles.Delete(root);

    [Fact]
    public void EveryMcpToolIsACommandWithTheSameArguments()
    {
        var methods = Type.GetType("TravelRepo.Mcp.TripTools, TravelRepo.Mcp", true)!.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<McpServerToolAttribute>())).Where(m => m.Attribute is not null).ToArray();
        Assert.Equal(methods.Select(m => m.Attribute!.Name).Order(StringComparer.Ordinal), Tools.All.Select(t => t.Name).Order(StringComparer.Ordinal));
        foreach (var (method, attribute) in methods)
        {
            var tool = Tools.ByName[attribute!.Name!];
            Assert.True(attribute.ReadOnly == tool.ReadOnly, tool.Name + " read-only");
            var read = new List<(string Name, string Type, object? Default, bool Required)>();
            Assert.Throws<NullReferenceException>(() => { _ = tool.Run(null!, new Arguments([]) { Recorder = read }, default); });
            var expected = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken))
                .Select(p => (p.Name!, Kind(p.ParameterType), p.HasDefaultValue ? p.DefaultValue : null, !p.HasDefaultValue)).ToList();
            Assert.Equal(expected, read);
        }
    }

    private static string Kind(Type type) => type == typeof(string) ? "string" : type == typeof(int) ? "int" : type == typeof(bool) ? "bool" : type == typeof(string[]) ? "string[]" : type == typeof(JsonElement) ? "json" : type.Name;

    [Fact]
    public async Task ClientsOpenATripReadItAndEditIt()
    {
        var info = await Ok(0, "info");
        Assert.Equal(TravelRepo.Core.TravelRepoInfo.FormatVersion, info["format_version"]!.GetValue<string>());
        Assert.Contains("get_schedule", info["commands"]!["session"]!.AsArray().Select(c => c!.GetValue<string>()));

        var session = (await Ok(0, "open", new JsonObject { ["root"] = Trip(), ["recovery_root"] = Path.Combine(root, "recovery") }))["session"]!.GetValue<ulong>();
        Assert.Equal("Tokyo", (await Ok(session, "get_trip"))["title"]!.GetValue<string>());
        var snapshot = await Ok(session, "snapshot");
        Assert.Contains(snapshot["entities"]!.AsArray(), e => e!["path"]!.GetValue<string>() == "travel.yaml");
        Assert.Empty(snapshot["diagnostics"]!.AsArray());

        var created = await Ok(session, "create_entity", new JsonObject { ["type"] = "person", ["data"] = new JsonObject { ["title"] = "Sam" } });
        var id = created["id"]!.GetValue<string>();
        Assert.Equal("Sam", (await Ok(session, "get_entity", new JsonObject { ["id"] = id }))["entity"]!["title"]!.GetValue<string>());
        Assert.Equal(JsonValueKind.String, (await Ok(session, "read_guide")).GetValueKind());

        Assert.Equal("command.unknown", await Failure(session, "fly_to_the_moon"));
        Assert.Equal("argument.missing", await Failure(session, "get_entity"));
        Assert.Equal("argument.invalid", await Failure(session, "list_entities", new JsonObject { ["limit"] = "many" }));
        Assert.Equal("command.unknown", await Failure(0, "get_trip"));
        Assert.Equal("session.not_trip", await Failure(0, "open", new JsonObject { ["root"] = root }));

        Exports.Close(session);
        Assert.Equal("session.unknown", await Failure(session, "get_trip"));
    }

    [Fact]
    public async Task ReadOnlySessionsRefuseEdits()
    {
        var session = (await Ok(0, "open", new JsonObject { ["root"] = Trip(), ["recovery_root"] = Path.Combine(root, "recovery"), ["read_only"] = true }))["session"]!.GetValue<ulong>();
        Assert.Equal("session.read_only", await Failure(session, "create_entity", new JsonObject { ["type"] = "person", ["data"] = new JsonObject { ["title"] = "Sam" } }));
    }

    [Fact]
    public async Task ExportsFollowTheSessionCulture()
    {
        var trip = Trip();
        async Task<string> Plan(string culture)
        {
            var session = (await Ok(0, "open", new JsonObject { ["root"] = trip, ["recovery_root"] = Path.Combine(root, "recovery"), ["culture"] = culture }))["session"]!.GetValue<ulong>();
            return (await Ok(session, "export_text"))["text"]!.GetValue<string>();
        }
        Assert.Contains("Mai", await Plan("de-DE"));
        Assert.Contains("May", await Plan("en-NZ"));
        Assert.Equal("argument.invalid", await Failure(0, "open", new JsonObject { ["root"] = trip, ["culture"] = "xx-not-a-culture" }));
    }

    [Fact]
    public async Task VersionsAndSyncStateWorkThroughLibGit2()
    {
        var session = (await Ok(0, "open", new JsonObject { ["root"] = Trip(), ["recovery_root"] = Path.Combine(root, "recovery") }))["session"]!.GetValue<ulong>();
        await Ok(session, "initialize_git", new JsonObject { ["name"] = "Alex", ["email"] = "alex@example.invalid" });
        await Ok(session, "create_entity", new JsonObject { ["type"] = "person", ["data"] = new JsonObject { ["title"] = "Sam" } });
        await Ok(session, "create_version", new JsonObject { ["message"] = "Add Sam" });
        Assert.Contains((await Ok(session, "list_versions"))["versions"]!.AsArray(), v => v!["message"]!.GetValue<string>() == "Add Sam");
        var status = await Ok(session, "sync_status");
        Assert.Equal("local_only", status["state"]!.GetValue<string>()); Assert.False(status["has_local_changes"]!.GetValue<bool>(), status.ToJsonString());
    }

    [Fact]
    public async Task CredentialsComeFromTheHostAndCanBeCancelled()
    {
        var credentials = new HostCredentials();
        Assert.Null(await credentials.GetAsync(new Uri("https://github.com/alex-travels/trip.git")));

        credentials.Request = (request, origin) => credentials.Reply(request, origin == "https://github.com" ? """{"username":"x-access-token","password":"test-token"}""" : null);
        Assert.Equal(new GitCredential("x-access-token", "test-token"), await credentials.GetAsync(new Uri("https://github.com/alex-travels/trip.git")));
        Assert.Null(await credentials.GetAsync(new Uri("https://example.org/trip.git")));

        credentials.Request = (_, _) => { };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => credentials.GetAsync(new Uri("https://github.com/alex-travels/trip.git"), cancel.Token));
    }

    [Fact]
    public async Task AppsEditWithRevisionsAndGetConflictsWhenTheTripChanged()
    {
        var created = await Ok(0, "create_trip", new JsonObject { ["root"] = Path.Combine(root, "spring"), ["recovery_root"] = Path.Combine(root, "recovery"), ["title"] = "Spring in Japan", ["timezone"] = "Asia/Tokyo" });
        Assert.Equal("trip.exists", await Failure(0, "create_trip", new JsonObject { ["root"] = Path.Combine(root, "spring"), ["title"] = "Again" }));
        var session = (await Ok(0, "open", new JsonObject { ["root"] = created["root"]!.GetValue<string>(), ["recovery_root"] = Path.Combine(root, "recovery") }))["session"]!.GetValue<ulong>();
        var revision = (await Ok(session, "snapshot"))["revision"]!.GetValue<string>();

        var alex = TravelRepo.Core.Entity.Create("person", "Alex"); var sam = TravelRepo.Core.Entity.Create("person", "Sam");
        var applied = await Ok(session, "apply", new JsonObject
        {
            ["revision"] = revision,
            ["edits"] = new JsonArray(new JsonObject { ["id"] = alex.Id.ToString(), ["data"] = alex.Data.DeepClone() }, new JsonObject { ["id"] = sam.Id.ToString(), ["data"] = sam.Data.DeepClone() }),
            ["resources"] = new JsonArray(new JsonObject { ["path"] = "documents/" + alex.Id + ".md", ["base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("Window seat to Kyoto.")) })
        });
        var after = applied["revision"]!.GetValue<string>();
        Assert.NotEqual(revision, after);
        Assert.Equal(after, (await Ok(session, "revision"))["revision"]!.GetValue<string>());
        Assert.Equal("Window seat to Kyoto.", Encoding.UTF8.GetString(Convert.FromBase64String((await Ok(session, "read_resource", new JsonObject { ["path"] = "documents/" + alex.Id + ".md" }))["base64"]!.GetValue<string>())));
        Assert.Equal("resource.missing", await Failure(session, "read_resource", new JsonObject { ["path"] = "documents/none.md" }));

        // An edit based on the old revision must not overwrite what happened since.
        Assert.Equal("repository.changed", await Failure(session, "apply", new JsonObject { ["revision"] = revision, ["edits"] = new JsonArray(new JsonObject { ["id"] = sam.Id.ToString(), ["data"] = null }) }));
        Assert.Equal("repository.changed", await Failure(session, "apply", new JsonObject { ["revision"] = "0000000000000000", ["edits"] = new JsonArray(new JsonObject { ["id"] = sam.Id.ToString(), ["data"] = null }) }));
        await Ok(session, "apply", new JsonObject { ["revision"] = after, ["edits"] = new JsonArray(new JsonObject { ["id"] = sam.Id.ToString(), ["data"] = null }) });
        Assert.DoesNotContain((await Ok(session, "snapshot"))["entities"]!.AsArray(), e => e!["data"]!["id"]!.GetValue<string>() == sam.Id.ToString());

        var readOnly = (await Ok(0, "open", new JsonObject { ["root"] = created["root"]!.GetValue<string>(), ["recovery_root"] = Path.Combine(root, "recovery"), ["read_only"] = true }))["session"]!.GetValue<ulong>();
        Assert.Equal("session.read_only", await Failure(readOnly, "apply", new JsonObject { ["revision"] = after, ["edits"] = new JsonArray() }));
    }

    [Fact]
    public async Task AgendaAndSearchGiveStructuredResults()
    {
        var session = (await Ok(0, "open", new JsonObject { ["root"] = Trip(), ["recovery_root"] = Path.Combine(root, "recovery") }))["session"]!.GetValue<ulong>();
        var agenda = await Ok(session, "agenda");
        var flight = agenda["days"]!.AsArray().SelectMany(d => d!["entries"]!.AsArray()).First(e => e!["title"]!.GetValue<string>() == "Flight to Tokyo")!;
        Assert.Equal("2027-05-12T11:20:00Z", flight["start"]!.GetValue<string>());
        Assert.Equal("Europe/Berlin", flight["zone"]!.GetValue<string>());
        Assert.Equal("Asia/Tokyo", flight["end_zone"]!.GetValue<string>());
        Assert.Equal("transport", flight["kind"]!.GetValue<string>());

        var hit = Assert.Single((await Ok(session, "find", new JsonObject { ["query"] = "fligh tok", ["kinds"] = new JsonArray("plan") }))["hits"]!.AsArray())!;
        Assert.Equal("plan", hit["kind"]!.GetValue<string>());
        var match = hit["matches"]![0]!;
        Assert.Equal("title", match["field"]!.GetValue<string>());
        Assert.Equal("Fligh", match["text"]!.GetValue<string>().Substring(match["ranges"]![0]!["start"]!.GetValue<int>(), match["ranges"]![0]!["length"]!.GetValue<int>()));
        Assert.Equal("argument.invalid", await Failure(session, "find", new JsonObject { ["query"] = "x", ["kinds"] = new JsonArray("spaceship") }));
    }

    [Fact]
    public async Task VariantsAreMergedWithAnsweredConflicts()
    {
        var created = await Ok(0, "create_trip", new JsonObject { ["root"] = Path.Combine(root, "kyoto"), ["recovery_root"] = Path.Combine(root, "recovery"), ["title"] = "Kyoto", ["git_name"] = "Alex", ["git_email"] = "alex@example.invalid" });
        var session = (await Ok(0, "open", new JsonObject { ["root"] = created["root"]!.GetValue<string>(), ["recovery_root"] = Path.Combine(root, "recovery") }))["session"]!.GetValue<ulong>();
        var temple = (await Ok(session, "create_entity", new JsonObject { ["type"] = "place", ["data"] = new JsonObject { ["title"] = "Kiyomizu-dera" } }))["id"]!.GetValue<string>();
        await Ok(session, "create_version", new JsonObject { ["message"] = "Add the temple" });

        async Task Rename(string title, string message)
        {
            var entity = (await Ok(session, "get_entity", new JsonObject { ["id"] = temple }))["entity"]!.AsObject(); entity["title"] = title;
            await Ok(session, "apply", new JsonObject { ["revision"] = (await Ok(session, "revision"))["revision"]!.DeepClone(), ["edits"] = new JsonArray(new JsonObject { ["id"] = temple, ["data"] = entity.DeepClone() }) });
            await Ok(session, "create_version", new JsonObject { ["message"] = message });
        }
        await Ok(session, "create_variant", new JsonObject { ["branch"] = "variants/early-start", ["title"] = "Early start" });
        await Rename("Kiyomizu-dera at dawn", "Go early");
        var variants = (await Ok(session, "variants"))["variants"]!.AsArray();
        Assert.Contains(variants, v => v!["branch"]!.GetValue<string>() == "variants/early-start" && v["current"]!.GetValue<bool>());
        var primary = variants.First(v => v!["primary"]!.GetValue<bool>())!["branch"]!.GetValue<string>();
        await Ok(session, "switch_variant", new JsonObject { ["branch"] = primary });
        await Rename("Kiyomizu-dera at sunset", "Go late");

        var preview = await Ok(session, "merge_preview", new JsonObject { ["branch"] = "variants/early-start" });
        Assert.False(preview["can_apply"]!.GetValue<bool>());
        var conflict = Assert.Single(preview["conflicts"]!.AsArray())!;
        Assert.Equal("Kiyomizu-dera at sunset", conflict["current"]!.GetValue<string>());
        Assert.Equal("Kiyomizu-dera at dawn", conflict["incoming"]!.GetValue<string>());
        Assert.Equal("merge.unresolved", await Failure(session, "merge_apply", new JsonObject { ["branch"] = "variants/early-start" }));

        var resolutions = new JsonObject { [conflict["key"]!.GetValue<string>()] = "Kiyomizu-dera at dawn" };
        Assert.True((await Ok(session, "merge_preview", new JsonObject { ["branch"] = "variants/early-start", ["resolutions"] = resolutions.DeepClone() }))["can_apply"]!.GetValue<bool>());
        Assert.NotEmpty((await Ok(session, "merge_apply", new JsonObject { ["branch"] = "variants/early-start", ["resolutions"] = resolutions.DeepClone() }))["commit"]!.GetValue<string>());
        Assert.Equal("Kiyomizu-dera at dawn", (await Ok(session, "get_entity", new JsonObject { ["id"] = temple }))["entity"]!["title"]!.GetValue<string>());
        Assert.Contains((await Ok(session, "list_versions"))["versions"]!.AsArray(), v => v!["message"]!.GetValue<string>().StartsWith("Merge Early start", StringComparison.Ordinal));
    }

    private string Trip()
    {
        var trip = Path.Combine(root, "trip-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "tokyo"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(trip, Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "tokyo"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        return trip;
    }

    private static async Task<JsonNode> Ok(ulong session, string command, JsonObject? arguments = null)
    {
        var response = await Call(session, command, arguments);
        Assert.True(response["ok"]!.GetValue<bool>(), command + ": " + response.ToJsonString());
        return response["result"]!;
    }

    private static async Task<string> Failure(ulong session, string command, JsonObject? arguments = null)
    {
        var response = await Call(session, command, arguments);
        Assert.False(response["ok"]!.GetValue<bool>(), command + ": " + response.ToJsonString());
        Assert.NotEmpty(response["error"]!["message"]!.GetValue<string>());
        return response["error"]!["code"]!.GetValue<string>();
    }

    private static async Task<JsonNode> Call(ulong session, string command, JsonObject? arguments)
    {
        var context = (nint)Interlocked.Increment(ref lastContext);
        var answer = Answers[context] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.NotEqual(0ul, Exports.Call(session, command, arguments?.ToJsonString(), context));
        var text = await answer.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Answers.TryRemove(context, out _);
        return JsonNode.Parse(text)!;
    }

    /// <summary>Calls the exported functions through function pointers, as native code does.</summary>
    private static unsafe class Exports
    {
        public static ulong Call(ulong session, string command, string? arguments, nint context)
        {
            var name = Encoding.UTF8.GetBytes(command + "\0"); var json = arguments is null ? null : Encoding.UTF8.GetBytes(arguments + "\0");
            delegate* unmanaged<ulong, byte*, byte*, delegate* unmanaged<nint, byte*, void>, nint, ulong> call = &NativeApi.Call;
            fixed (byte* n = name) fixed (byte* a = json) return call(session, n, a, &Respond, context);
        }

        public static void Close(ulong session)
        {
            delegate* unmanaged<ulong, void> close = &NativeApi.Close;
            close(session);
        }

        [UnmanagedCallersOnly]
        private static void Respond(nint context, byte* response) => Answers[context].SetResult(Marshal.PtrToStringUTF8((nint)response)!);
    }
}
