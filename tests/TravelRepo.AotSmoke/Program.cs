using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using NodaTime;
using TravelRepo.Core;
using TravelRepo.Documents;
using TravelRepo.Git.LibGit2;
using TravelRepo.Merge;
using TravelRepo.Plugins;
using TravelRepo.Providers;
using TravelRepo.Providers.GitHub;
using TravelRepo.Providers.Weather;
using TravelRepo.Repository;
using TravelRepo.Serialization;

// Runs the TravelRepo libraries compiled with NativeAOT on the samples. Usage: TravelRepo.AotSmoke <samples-directory>
if (args.Length != 1 || !Directory.Exists(args[0])) { Console.Error.WriteLine("Usage: TravelRepo.AotSmoke <samples-directory>"); return 2; }
if (RuntimeFeature.IsDynamicCodeSupported) Console.WriteLine("Note: dynamic code is supported, so this is not a NativeAOT build.");
var failures = 0;
var work = Path.Combine(Path.GetTempPath(), "travelrepo-aot-" + Guid.NewGuid().ToString("N"));
async Task Step(string name, Func<Task<string>> run)
{
    try { Console.WriteLine($"ok    {name}: {await run()}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}"); }
}
try
{
    foreach (var sample in Directory.EnumerateDirectories(args[0]).Where(d => File.Exists(Path.Combine(d, "travel.yaml"))).Order(StringComparer.Ordinal))
    {
        var name = Path.GetFileName(sample);
        await Step("read and validate " + name, async () =>
        {
            var state = await new TravelRepository(sample, Path.Combine(work, "recovery-" + name)).ReadAsync();
            var problems = state.Diagnostics.Concat(SemanticValidation.Validate(state.Trip)).Where(d => d.Severity == Severity.Error).ToArray();
            if (problems.Length > 0) throw new InvalidOperationException(string.Join("; ", problems.Select(d => d.Code)));
            foreach (var file in state.Files.Values)
                if (!JsonNode.DeepEquals(file.Entity.Data, YamlCodec.Read(YamlCodec.Write(file.Entity)).Data)) throw new InvalidOperationException("YAML round trip changed " + file.Path);
            _ = SemanticMerge.Plan(state.Trip, state.Trip, state.Trip); _ = ChangeSummary.Between(state.Trip, state.Trip);
            return $"{state.Files.Count} files";
        });
    }
    await Step("write with journal", async () =>
    {
        var repo = new TravelRepository(Path.Combine(work, "trip"), Path.Combine(work, "recovery"));
        await repo.InitializeAsync(Entity.CreateTrip("Spring in Japan"));
        var alex = Entity.Create("person", "Alex"); var state = await repo.ApplyAsync(await repo.ReadAsync(), [new(alex.Id, alex)]);
        await repo.RecoverAsync();
        return $"{state.Files.Count} files, {Directory.EnumerateFiles(repo.RecoveryRoot, "*.complete").Count()} journals";
    });
    await Step("schema errors", () =>
    {
        var diagnostic = SchemaValidation.Validate(YamlCodec.Read("schema: travelrepo/1.0\ntype: person\nid: not-a-uuid\ntitle: Alex\n")).Single();
        return Task.FromResult(JsonNode.Parse(diagnostic.Message)!["valid"]!.GetValue<bool>() ? throw new InvalidOperationException("valid") : diagnostic.Code);
    });
    await Step("exchange rates", () =>
    {
        var path = Path.Combine(work, "rates.json"); new ExchangeRates(new DateOnly(2026, 4, 1), new Dictionary<string, decimal> { ["JPY"] = 160.5m }).Save(path);
        return Task.FromResult(ExchangeRates.Load(path)!.Convert(100m, "EUR", "JPY")!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    });
    await Step("weather cache", () =>
    {
        var cache = new WeatherCache(Path.Combine(work, "weather")); var tokyo = new GeoPoint(35.6812, 139.7671); var now = Instant.FromUtc(2026, 4, 2, 0, 0);
        cache.Store(new WeatherForecast("open-meteo", tokyo, now, now + Duration.FromHours(6), [new WeatherHour(now, 1, 14.5, 20, 0.2, 12, null, WeatherCondition.Clear)]));
        cache.Store(tokyo, [new ClimateDay(new LocalDate(2026, 4, 2), 19, 9.5, 0.3, 3.1, 30)]);
        return Task.FromResult($"{cache.Forecast("open-meteo", tokyo)!.Hours.Count} hour, {cache.Climate(tokyo).Count} climate day");
    });
    await Step("plugin manifest", () => Task.FromResult(PluginManifest.Parse("""{"id":"org.example.walking","version":"1.0.0","minimumApiVersion":1,"entryAssembly":"Walking.dll","capabilities":[],"permissions":[]}""").DisplayName));
    await Step("GitHub request body", async () =>
    {
        string? body = null;
        var http = new HttpClient(new Handler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"full_name":"alex-travels/trip","clone_url":"https://example.invalid/trip.git","html_url":"https://example.invalid/trip","private":true}""") };
        }));
        var secrets = new Secrets(); await secrets.WriteAsync("github.user-token", "test-token");
        var repository = await new GitHubProvider(http, secrets, "client").CreateAsync("trip");
        return JsonNode.Parse(body!)!["private"]!.GetValue<bool>() && repository.FullName == "alex-travels/trip" ? body! : throw new InvalidOperationException(body);
    });
    await Step("mail", () => Task.FromResult(Mail.Read(Encoding.UTF8.GetBytes("From: Alex <alex@example.com>\r\nTo: Sam <sam@example.com>\r\nSubject: Shinkansen Tokyo - Kyoto\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nSeats 7 and 8.\r\n")).Subject ?? "no subject"));
    await Step("PDF text", () => Task.FromResult(DocumentText.Extract(MinimalPdf("Nara Hotel"), "application/pdf")?.Trim() is "Nara Hotel" ? "Nara Hotel" : throw new InvalidOperationException("text missing")));
    await Step("libgit2 versions", async () =>
    {
        var repo = new TravelRepository(Path.Combine(work, "versioned"), Path.Combine(work, "versioned-recovery"));
        await repo.InitializeAsync(Entity.CreateTrip("Spring in Japan"));
        var git = new TravelRepo.Git.GitRepository(repo, new LibGit2Backend());
        await git.InitializeAsync("Alex", "alex@example.invalid");
        var sam = Entity.Create("person", "Sam"); await repo.ApplyAsync(await repo.ReadAsync(), [new(sam.Id, sam)]);
        await git.CreateVersionAsync("Add Sam");
        var history = await git.HistoryAsync();
        return history.Any(v => v.Message.StartsWith("Add Sam", StringComparison.Ordinal)) ? $"{history.Count} versions" : throw new InvalidOperationException("version missing: " + string.Join(" / ", history.Select(v => v.Message + "@" + v.Commit)));
    });
    await Step("libgit2 sync", async () =>
    {
        var backend = new LibGit2Backend();
        var remote = Path.Combine(work, "remote.git");
        using (var bare = new LibGit2Sharp.Repository(LibGit2Sharp.Repository.Init(remote, isBare: true))) bare.Refs.UpdateTarget("HEAD", "refs/heads/main");
        var alex = new TravelRepository(Path.Combine(work, "alex"), Path.Combine(work, "alex-recovery"));
        await alex.InitializeAsync(Entity.CreateTrip("Spring in Japan"));
        var alexGit = new TravelRepo.Git.GitRepository(alex, backend);
        await alexGit.InitializeAsync("Alex", "alex@example.invalid"); await alexGit.AddRemoteAsync("origin", remote);
        if (await alexGit.SyncAsync("origin") != TravelRepo.Git.SyncState.Synced) throw new InvalidOperationException("first push");
        await TravelRepo.Git.GitRepository.CloneAsync(backend, remote, Path.Combine(work, "sam"));
        var sam = new TravelRepository(Path.Combine(work, "sam"), Path.Combine(work, "sam-recovery"));
        var samGit = new TravelRepo.Git.GitRepository(sam, backend); await samGit.SetIdentityAsync("Sam", "sam@example.invalid");
        var nara = Entity.Create("place", "Nara Park"); await sam.ApplyAsync(await sam.ReadAsync(), [new(nara.Id, nara)]);
        await samGit.CreateVersionAsync("Add Nara Park");
        if (await samGit.SyncAsync("origin") != TravelRepo.Git.SyncState.Synced) throw new InvalidOperationException("second push");
        if (await alexGit.SyncAsync("origin") != TravelRepo.Git.SyncState.Synced) throw new InvalidOperationException("pull");
        return (await alex.ReadAsync()).Trip.Find(nara.Id)?.Title ?? throw new InvalidOperationException("Nara Park did not arrive");
    });
    await Step("C ABI", async () =>
    {
        var info = await NativeCalls.Call(0, "info", null);
        var opened = await NativeCalls.Call(0, "open", new JsonObject { ["root"] = Path.Combine(work, "alex"), ["recovery_root"] = Path.Combine(work, "abi-recovery"), ["culture"] = "de-DE" }.ToJsonString());
        var session = opened["result"]!["session"]!.GetValue<ulong>();
        var created = await NativeCalls.Call(session, "create_entity", """{"type":"person","data":{"title":"Sam"}}""");
        var version = await NativeCalls.Call(session, "create_version", """{"message":"Add Sam"}""");
        var versions = await NativeCalls.Call(session, "list_versions", null);
        var missing = await NativeCalls.Call(session, "get_entity", null);
        NativeCalls.Close(session);
        if (!created["ok"]!.GetValue<bool>() || !version["ok"]!.GetValue<bool>()) throw new InvalidOperationException(created.ToJsonString() + version.ToJsonString());
        if (missing["error"]!["code"]!.GetValue<string>() != "argument.missing") throw new InvalidOperationException(missing.ToJsonString());
        var newest = versions["result"]!["versions"]![0]!["message"]!.GetValue<string>();
        return newest == "Add Sam" ? $"TravelRepo {info["result"]!["version"]}, newest version \"{newest}\"" : throw new InvalidOperationException(versions.ToJsonString());
    });
    await Step("libgit2", async () =>
    {
        var directory = Path.Combine(work, "git"); Directory.CreateDirectory(directory);
        var result = await new LibGit2Backend().ExecuteAsync(directory, ["init"]);
        return result.ExitCode == 0 ? (await new LibGit2Backend().ExecuteAsync(directory, ["--version"])).Output.Trim() : throw new InvalidOperationException(result.Error);
    });
}
finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
Console.WriteLine(failures == 0 ? "All steps passed." : $"{failures} steps failed.");
return failures == 0 ? 0 : 1;

static byte[] MinimalPdf(string text)
{
    var stream = $"BT /F1 12 Tf 10 50 Td ({text}) Tj ET";
    string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 100] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"];
    var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
    for (var i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
    var xref = pdf.Length; pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
    foreach (var offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
    pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    return Encoding.ASCII.GetBytes(pdf.ToString());
}

/// <summary>Calls the exported C functions of TravelRepo.Native through function pointers, as native code does.</summary>
internal static class NativeCalls
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, TaskCompletionSource<string>> Answers = new();
    private static long last;

    public static async Task<JsonNode> Call(ulong session, string command, string? arguments)
    {
        var context = (nint)Interlocked.Increment(ref last);
        var answer = Answers[context] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Start(session, command, arguments, context);
        var text = await answer.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Answers.TryRemove(context, out _);
        return JsonNode.Parse(text)!;
    }

    private static unsafe void Start(ulong session, string command, string? arguments, nint context)
    {
        var name = Encoding.UTF8.GetBytes(command + "\0"); var json = arguments is null ? null : Encoding.UTF8.GetBytes(arguments + "\0");
        delegate* unmanaged<ulong, byte*, byte*, delegate* unmanaged<nint, byte*, void>, nint, ulong> call = &TravelRepo.Native.NativeApi.Call;
        fixed (byte* n = name) fixed (byte* a = json) call(session, n, a, &Respond, context);
    }

    public static unsafe void Close(ulong session) { delegate* unmanaged<ulong, void> close = &TravelRepo.Native.NativeApi.Close; close(session); }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static unsafe void Respond(nint context, byte* response) => Answers[context].SetResult(System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)response)!);
}

internal sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}

internal sealed class Secrets : ISecretStore
{
    private readonly Dictionary<string, string> values = [];
    public Task<string?> ReadAsync(string key, CancellationToken ct = default) => Task.FromResult(values.GetValueOrDefault(key));
    public Task WriteAsync(string key, string value, CancellationToken ct = default) { values[key] = value; return Task.CompletedTask; }
    public Task DeleteAsync(string key, CancellationToken ct = default) { values.Remove(key); return Task.CompletedTask; }
}
