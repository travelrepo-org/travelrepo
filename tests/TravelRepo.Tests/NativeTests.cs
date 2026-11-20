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
