using System.Globalization;
using System.Text;
using LibGit2Sharp;
using TravelRepo.Core;
using GitRepo = LibGit2Sharp.Repository;
using LibGit2Sharp.Handlers;

namespace TravelRepo.Git.LibGit2;

/// <summary>
/// Git through libgit2 instead of the <c>git</c> program, for platforms without it (Android, iOS). It understands the
/// commands <see cref="GitRepository"/> sends and answers in the same output format and with the same messages, so
/// error classification works unchanged. Other commands fail with exit code 129.
/// </summary>
/// <remarks>
/// Spike: the command-line shape is kept so the existing Git test suite can run against both backends unchanged.
/// </remarks>
public sealed class LibGit2Backend(ICredentialBroker? credentialBroker = null) : IGitBackend
{
    static LibGit2Backend() => ManagedHttpTransport.Register();

    /// <summary>The name LibGit2Sharp loads libgit2 by, which contains the libgit2 commit it was built for.</summary>
    public const string NativeLibraryName = "git2-5853918";

    /// <summary>
    /// Loads libgit2 from <paramref name="path"/> and uses it where the system cannot find it by
    /// <see cref="NativeLibraryName"/>, such as on iOS, where it ships as a framework inside the app
    /// (<c>Frameworks/git2.framework/git2</c>, built by <c>eng/build-libgit2-ios.sh</c>). Call it once, before the first
    /// Git command.
    /// </summary>
    /// <exception cref="DllNotFoundException">The library cannot be loaded; the message says why.</exception>
    public static void UseNativeLibrary(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var handle = System.Runtime.InteropServices.NativeLibrary.Load(path);
        System.Runtime.Loader.AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, name) =>
            name == NativeLibraryName && assembly == typeof(GitRepo).Assembly ? handle : IntPtr.Zero;
    }

    public async Task<GitResult> ExecuteAsync(string directory, IReadOnlyList<string> arguments, string? input = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) throw new DomainException("git.folder_missing", "The trip folder no longer exists: " + directory);
        var args = arguments.ToList();
        if (args.Count == 0) return Usage();
        // Network commands need the sign-in before libgit2 asks for it synchronously.
        GitCredential? credential = null;
        if (args[0] is "fetch" or "push" or "clone" && credentialBroker is not null) credential = await credentialBroker.GetAsync(new Uri("https://github.com"), cancellationToken);
        return await Task.Run(() =>
        {
            try { return Execute(directory, args, input, credential); }
            catch (RepositoryNotFoundException) { return Fail("fatal: not a git repository (or any of the parent directories): .git"); }
            catch (LockedFileException ex) { return Fail("fatal: Unable to create '" + ex.Message + "': File exists. Another git process seems to be running; index.lock"); }
            catch (LibGit2SharpException ex) when (ex.Message.Contains("not owned by current user", StringComparison.OrdinalIgnoreCase)) { return Fail("fatal: detected dubious ownership in repository at '" + directory + "'"); }
            catch (LibGit2SharpException ex) { return Fail("fatal: " + ex.Message); }
        }, cancellationToken);
    }

    private static GitResult Execute(string directory, List<string> args, string? input, GitCredential? credential)
    {
        switch (args[0])
        {
            case "--version": return Ok("git version libgit2 " + LibGit2Version() + "\n");
            case "init": return Init(directory, args);
            case "clone": return Clone(directory, args, credential);
        }
        using var repo = new GitRepo(GitRepo.Discover(directory) ?? throw new RepositoryNotFoundException(directory));
        return args[0] switch
        {
            "merge" or "rebase" or "cherry-pick" or "revert" when args.Contains("--abort") => Abort(repo, args[0]),
            "config" => Config(repo, args),
            "status" => Status(repo, args),
            "add" => Add(repo, args),
            "commit" => Commit(repo, args, input),
            "diff" when args.SequenceEqual(["diff", "--cached", "--name-only", "-z"]) => StagedPaths(repo),
            "ls-files" => Ok(string.Concat(repo.Index.Select(e => e.Path + (args.Contains("-z") ? "\0" : "\n")))),
            "for-each-ref" => ForEachRef(repo, args),
            "log" => Log(repo, args),
            "rev-parse" => RevParse(repo, args),
            "symbolic-ref" => SymbolicRef(repo, args),
            "show" => Show(repo, args),
            "ls-tree" => LsTree(repo, args),
            "cat-file" when args.Count == 2 && args[1] == "--batch" => CatFile(repo, input ?? ""),
            "check-ref-format" => CheckRefFormat(args),
            "switch" => Switch(repo, args),
            "branch" => Branch(repo, args),
            "update-ref" => UpdateRef(repo, args),
            "merge-base" => MergeBase(repo, args),
            "merge" => Merge(repo, args),
            "rev-list" => RevList(repo, args),
            "remote" => Remote(repo, args),
            "fetch" => Fetch(repo, args, credential),
            "push" => Push(repo, args, credential),
            "write-tree" => Ok(repo.ObjectDatabase.CreateTree(repo.Index).Sha + "\n"),
            "commit-tree" => CommitTree(repo, args, input),
            _ => Usage()
        };
    }

    private static GitResult Ok(string output) => new(0, output, Encoding.UTF8.GetBytes(output));
    private static GitResult Ok(byte[] bytes) => new(0, Encoding.UTF8.GetString(bytes), bytes);
    private static GitResult Fail(string error, int exitCode = 128) => new(exitCode, "", [], error + "\n");
    private static GitResult Quiet(int exitCode = 1) => new(exitCode, "", [], "");
    private static GitResult Usage() => Fail("usage: this command is not supported by the libgit2 backend", 129);
    private static string LibGit2Version() => typeof(GitRepo).Assembly.GetName().Version + " (libgit2 " + GlobalSettings.Version.LibGit2CommitSha + ")";
    /// <summary>The operands after options; a <c>--</c> ends option parsing.</summary>
    private static List<string> Operands(IReadOnlyList<string> args)
    {
        var result = new List<string>(); var options = true;
        foreach (var a in args.Skip(1)) { if (options && a == "--") { options = false; continue; } if (!options || !a.StartsWith('-')) result.Add(a); }
        return result;
    }

    // ---- setup -------------------------------------------------------------------------------------------------

    private static GitResult Init(string directory, List<string> args)
    {
        var bare = args.Contains("--bare"); var branch = args.IndexOf("-b") is var b and >= 0 ? args[b + 1] : "main";
        var path = GitRepo.Init(directory, bare);
        using var repo = new GitRepo(path);
        // The branch does not exist before the first commit, so HEAD is written as a symbolic reference to it.
        repo.Refs.Add("HEAD", "refs/heads/" + branch, allowOverwrite: true);
        return Ok("Initialized empty Git repository in " + path + "\n");
    }

    private static GitResult Clone(string directory, List<string> args, GitCredential? credential)
    {
        var operands = Operands(args); var url = operands[0]; var destination = operands.Count > 1 ? operands[1] : Path.Combine(directory, Path.GetFileNameWithoutExtension(url.TrimEnd('/')));
        var options = new CloneOptions(); options.FetchOptions.CredentialsProvider = Credentials(credential);
        try { GitRepo.Clone(url, destination, options); return Ok(""); }
        catch (LibGit2SharpException ex) { return Fail(Transport(url, ex)); }
    }

    // ---- configuration -------------------------------------------------------------------------------------------

    private static GitResult Config(GitRepo repo, List<string> args)
    {
        var flags = args.Skip(1).Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToHashSet(); var rest = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var asBool = flags.Contains("--bool");
        if (flags.Contains("--get"))
        {
            var entry = repo.Config.Get<string>(rest[0]);
            if (entry is null) return Quiet();
            return Ok((asBool ? Bool(entry.Value) : entry.Value) + "\n");
        }
        if (flags.Contains("--unset")) return repo.Config.Unset(rest[0], ConfigurationLevel.Local) ? Ok("") : Quiet(5);
        repo.Config.Set(rest[0], asBool ? Bool(rest[1]) : rest[1], ConfigurationLevel.Local);
        return Ok("");
    }

    private static string Bool(string value) => value.Trim().ToLowerInvariant() is "true" or "yes" or "on" or "1" ? "true" : "false";

    // ---- working tree and index --------------------------------------------------------------------------------

    private static GitResult Status(GitRepo repo, List<string> args)
    {
        var paths = args.IndexOf("--") is var i and >= 0 ? args.Skip(i + 1).ToArray() : [];
        var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = false, IncludeIgnored = false, PathSpec = paths.Length > 0 ? paths : null, DisablePathSpecMatch = false });
        var z = args.Contains("-z"); var porcelain = args.Any(a => a.StartsWith("--porcelain", StringComparison.Ordinal));
        if (!porcelain) return Ok(status.IsDirty ? "Changes not staged for commit\n" : "nothing to commit, working tree clean\n");
        var output = new StringBuilder();
        foreach (var entry in status.Where(e => e.State != FileStatus.Unaltered && e.State != FileStatus.Ignored))
            output.Append(Code(entry.State)).Append(' ').Append(entry.FilePath).Append(z ? '\0' : '\n');
        return Ok(output.ToString());
    }

    private static string Code(FileStatus s)
    {
        if (s.HasFlag(FileStatus.NewInWorkdir) && !s.HasFlag(FileStatus.NewInIndex)) return "??";
        var x = s.HasFlag(FileStatus.NewInIndex) ? 'A' : s.HasFlag(FileStatus.ModifiedInIndex) ? 'M' : s.HasFlag(FileStatus.DeletedFromIndex) ? 'D' : s.HasFlag(FileStatus.RenamedInIndex) ? 'R' : s.HasFlag(FileStatus.TypeChangeInIndex) ? 'T' : ' ';
        var y = s.HasFlag(FileStatus.ModifiedInWorkdir) ? 'M' : s.HasFlag(FileStatus.DeletedFromWorkdir) ? 'D' : s.HasFlag(FileStatus.TypeChangeInWorkdir) ? 'T' : s.HasFlag(FileStatus.Conflicted) ? 'U' : ' ';
        return new string([x, y]);
    }

    private static GitResult Add(GitRepo repo, List<string> args)
    {
        Commands.Stage(repo, Operands(args));
        return Ok("");
    }

    private static GitResult Commit(GitRepo repo, List<string> args, string? input)
    {
        var message = args.IndexOf("-m") is var m and >= 0 ? args[m + 1] : input ?? "";
        var signature = repo.Config.BuildSignature(DateTimeOffset.Now);
        if (signature is null) return Fail("Author identity unknown\n*** Please tell me who you are.");
        try { repo.Commit(message, signature, signature, new CommitOptions { PrettifyMessage = true, AllowEmptyCommit = false }); return Ok(""); }
        catch (EmptyCommitException) { return Fail("nothing to commit, working tree clean", 1); }
    }

    private static GitResult StagedPaths(GitRepo repo)
    {
        var changes = repo.Diff.Compare<TreeChanges>(repo.Head.Tip?.Tree, DiffTargets.Index);
        return Ok(string.Concat(changes.Select(c => c.Path + "\0")));
    }

    private static GitResult CommitTree(GitRepo repo, List<string> args, string? input)
    {
        var tree = repo.Lookup<Tree>(args[1]); var parents = new List<Commit>();
        for (var i = 2; i < args.Count - 1; i++) if (args[i] == "-p") parents.Add(repo.Lookup<Commit>(args[i + 1]));
        var signature = repo.Config.BuildSignature(DateTimeOffset.Now);
        if (signature is null) return Fail("Author identity unknown");
        return Ok(repo.ObjectDatabase.CreateCommit(signature, signature, input ?? "", tree, parents, prettifyMessage: true).Sha + "\n");
    }

    // ---- references --------------------------------------------------------------------------------------------

    private static GitResult ForEachRef(GitRepo repo, List<string> args)
    {
        var format = args.FirstOrDefault(a => a.StartsWith("--format=", StringComparison.Ordinal))?["--format=".Length..] ?? "%(objectname) %(objecttype)\t%(refname)";
        var patterns = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var output = new StringBuilder();
        foreach (var reference in repo.Refs.OrderBy(r => r.CanonicalName, StringComparer.Ordinal))
        {
            if (reference.CanonicalName == "HEAD" || patterns.Count > 0 && !patterns.Any(p => Matches(reference.CanonicalName, p))) continue;
            var target = reference.ResolveToDirectReference()?.Target;
            output.Append(Format(repo, format, reference, target)).Append('\n');
        }
        return Ok(output.ToString());
    }

    private static bool Matches(string name, string pattern)
    {
        if (pattern.Contains('*')) return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: false);
        return name == pattern || name.StartsWith(pattern.EndsWith('/') ? pattern : pattern + "/", StringComparison.Ordinal);
    }

    private static string Format(GitRepo repo, string format, Reference reference, GitObject? target) =>
        System.Text.RegularExpressions.Regex.Replace(format.Replace("%00", "\0", StringComparison.Ordinal), @"%\((?<atom>[^)]+)\)", match =>
        {
            var commit = target.AsCommit();
            return match.Groups["atom"].Value switch
            {
                "refname" => reference.CanonicalName,
                "refname:short" => Short(reference.CanonicalName),
                "objectname" => target?.Sha ?? "",
                "objecttype" => target is Commit ? "commit" : target is TagAnnotation ? "tag" : target is Tree ? "tree" : "blob",
                "upstream" => Upstream(repo, reference.CanonicalName) ?? "",
                "upstream:track,nobracket" => Track(repo, reference.CanonicalName, commit),
                "authorname" => commit?.Author.Name ?? "",
                "committerdate:iso-strict" => commit is null ? "" : IsoStrict(commit.Committer.When),
                var atom => throw new LibGit2SharpException("unsupported format atom: " + atom)
            };
        });

    private static string Short(string name)
    {
        if (name.StartsWith("refs/heads/", StringComparison.Ordinal)) return name["refs/heads/".Length..];
        if (name.StartsWith("refs/remotes/", StringComparison.Ordinal)) { var rest = name["refs/remotes/".Length..]; return rest.EndsWith("/HEAD", StringComparison.Ordinal) ? rest[..^5] : rest; }
        if (name.StartsWith("refs/tags/", StringComparison.Ordinal)) return name["refs/tags/".Length..];
        return name.StartsWith("refs/", StringComparison.Ordinal) ? name["refs/".Length..] : name;
    }

    /// <summary>The remote-tracking reference a local branch follows, from its configuration, whether it exists or not.</summary>
    private static string? Upstream(GitRepo repo, string reference)
    {
        if (!reference.StartsWith("refs/heads/", StringComparison.Ordinal)) return null;
        var branch = reference["refs/heads/".Length..];
        var remote = repo.Config.Get<string>("branch." + branch + ".remote")?.Value; var merge = repo.Config.Get<string>("branch." + branch + ".merge")?.Value;
        if (remote is null || merge is null) return null;
        if (remote == ".") return merge;
        return merge.StartsWith("refs/heads/", StringComparison.Ordinal) ? "refs/remotes/" + remote + "/" + merge["refs/heads/".Length..] : null;
    }

    private static string Track(GitRepo repo, string reference, Commit? local)
    {
        var upstream = Upstream(repo, reference); if (upstream is null || local is null) return "";
        var theirs = repo.Refs[upstream]?.ResolveToDirectReference()?.Target.AsCommit();
        if (theirs is null) return "gone";
        var (ahead, behind) = AheadBehind(repo, local, theirs);
        return string.Join(", ", new[] { ahead > 0 ? "ahead " + ahead : null, behind > 0 ? "behind " + behind : null }.Where(p => p is not null));
    }

    private static (int Ahead, int Behind) AheadBehind(GitRepo repo, Commit left, Commit right) =>
        (Count(repo, [left], [right]), Count(repo, [right], [left]));

    private static int Count(GitRepo repo, IEnumerable<Commit> include, IEnumerable<Commit> exclude) =>
        repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = include.ToList(), ExcludeReachableFrom = exclude.ToList() }).Count();

    private static string IsoStrict(DateTimeOffset when) => when.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (when.Offset == TimeSpan.Zero ? "Z" : when.ToString("zzz", CultureInfo.InvariantCulture));

    private static GitObject? Resolve(GitRepo repo, string spec)
    {
        var peelToCommit = spec.EndsWith("^{commit}", StringComparison.Ordinal);
        if (peelToCommit) spec = spec[..^"^{commit}".Length];
        var found = repo.Lookup(spec);
        return peelToCommit ? found.AsCommit() : found;
    }

    private static Commit? ResolveCommit(GitRepo repo, string spec) => Resolve(repo, spec).AsCommit();

    private static GitResult RevParse(GitRepo repo, List<string> args)
    {
        if (args.Contains("--absolute-git-dir")) return Ok(Path.TrimEndingDirectorySeparator(repo.Info.Path) + "\n");
        var spec = Operands(args).Single(); var quiet = args.Contains("--quiet");
        var found = Resolve(repo, spec);
        if (found is null) return quiet ? Quiet() : Fail("fatal: ambiguous argument '" + spec + "': unknown revision or path not in the working tree.");
        return Ok(found.Sha + "\n");
    }

    private static GitResult SymbolicRef(GitRepo repo, List<string> args)
    {
        var head = repo.Refs.Head;
        if (head is not SymbolicReference symbolic) return args.Contains("--quiet") ? Quiet() : Fail("fatal: ref HEAD is not a symbolic ref");
        return Ok((args.Contains("--short") ? Short(symbolic.TargetIdentifier) : symbolic.TargetIdentifier) + "\n");
    }

    private static GitResult CheckRefFormat(List<string> args)
    {
        var name = Operands(args).Single();
        return Reference.IsValidName("refs/heads/" + name) && !name.StartsWith('-') ? Ok(name + "\n") : Fail("fatal: '" + name + "' is not a valid branch name");
    }

    private static GitResult UpdateRef(GitRepo repo, List<string> args)
    {
        var operands = Operands(args);
        if (args.Contains("-d"))
        {
            if (repo.Refs[operands[0]] is { } existing) repo.Refs.Remove(existing);
            return Ok("");
        }
        var name = operands[0]; var value = ResolveCommit(repo, operands[1]) ?? throw new LibGit2SharpException(operands[1] + ": not a valid SHA1");
        // HEAD updates the branch it points to, as Git does.
        if (name == "HEAD" && repo.Refs.Head is SymbolicReference head) name = head.TargetIdentifier;
        var current = repo.Refs[name]?.ResolveToDirectReference()?.Target;
        if (operands.Count > 2 && current?.Sha != ResolveCommit(repo, operands[2])?.Sha) return Fail("fatal: update_ref failed for ref '" + name + "': cannot lock ref '" + name + "': is at " + current?.Sha + " but expected " + operands[2]);
        repo.Refs.Add(name, value.Id, allowOverwrite: true);
        return Ok("");
    }

    private static GitResult Branch(GitRepo repo, List<string> args)
    {
        var operands = Operands(args);
        if (args.Contains("-D"))
        {
            if (repo.Head.FriendlyName == operands[0]) return Fail("error: Cannot delete branch '" + operands[0] + "' checked out", 1);
            repo.Branches.Remove(operands[0]); return Ok("");
        }
        if (args.Contains("--unset-upstream"))
        {
            var branch = operands[0];
            if (repo.Config.Get<string>("branch." + branch + ".merge") is null) return Fail("fatal: branch '" + branch + "' has no upstream information");
            repo.Config.Unset("branch." + branch + ".remote", ConfigurationLevel.Local); repo.Config.Unset("branch." + branch + ".merge", ConfigurationLevel.Local);
            return Ok("");
        }
        repo.Branches.Add(operands[0], ResolveCommit(repo, operands.Count > 1 ? operands[1] : "HEAD")!);
        return Ok("");
    }

    // ---- reading history ---------------------------------------------------------------------------------------

    private static GitResult Show(GitRepo repo, List<string> args)
    {
        var spec = Operands(args).Single(); var colon = spec.IndexOf(':');
        if (colon < 0) return Usage();
        var commit = ResolveCommit(repo, spec[..colon]);
        if (commit is null) return Fail("fatal: invalid object name '" + spec[..colon] + "'.");
        if (commit[spec[(colon + 1)..]]?.Target is not Blob blob) return Fail("fatal: path '" + spec[(colon + 1)..] + "' does not exist in '" + spec[..colon] + "'");
        return Ok(Content(blob));
    }

    private static byte[] Content(Blob blob)
    {
        using var stream = blob.GetContentStream(); using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }

    private static GitResult LsTree(GitRepo repo, List<string> args)
    {
        var commit = ResolveCommit(repo, Operands(args).Single()) ?? throw new LibGit2SharpException("Not a valid object name");
        var output = new StringBuilder(); var end = args.Contains("-z") ? '\0' : '\n';
        void Walk(Tree tree)
        {
            foreach (var entry in tree)
            {
                if (entry.TargetType == TreeEntryTargetType.Tree) { Walk((Tree)entry.Target); continue; }
                var type = entry.TargetType == TreeEntryTargetType.Blob ? "blob" : "commit";
                output.Append(Convert.ToString((int)entry.Mode, 8).PadLeft(6, '0')).Append(' ').Append(type).Append(' ').Append(entry.Target.Sha).Append('\t').Append(entry.Path.Replace('\\', '/')).Append(end);
            }
        }
        Walk(commit.Tree);
        return Ok(output.ToString());
    }

    private static GitResult CatFile(GitRepo repo, string input)
    {
        using var output = new MemoryStream();
        foreach (var id in input.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (repo.Lookup(id) is not Blob blob) { output.Write(Encoding.ASCII.GetBytes(id + " missing\n")); continue; }
            var content = Content(blob);
            output.Write(Encoding.ASCII.GetBytes(blob.Sha + " blob " + content.Length.ToString(CultureInfo.InvariantCulture) + "\n")); output.Write(content); output.WriteByte((byte)'\n');
        }
        return Ok(output.ToArray());
    }

    private static GitResult Log(GitRepo repo, List<string> args)
    {
        var limit = args.FirstOrDefault(a => a.Length > 1 && a[0] == '-' && char.IsDigit(a[1])) is { } n ? int.Parse(n[1..], CultureInfo.InvariantCulture) : int.MaxValue;
        var format = args.First(a => a.StartsWith("--format=", StringComparison.Ordinal))["--format=".Length..];
        if (repo.Info.IsHeadUnborn) return Fail("fatal: your current branch '" + repo.Head.FriendlyName + "' does not have any commits yet");
        var decorations = Decorations(repo); var output = new StringBuilder();
        foreach (var commit in repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = repo.Head, SortBy = CommitSortStrategies.Time | CommitSortStrategies.Topological }).Take(limit))
        {
            output.Append(System.Text.RegularExpressions.Regex.Replace(format, @"%(x00|aI|an|ae|cn|ce|H|P|B|D)", m => m.Groups[1].Value switch
            {
                "x00" => "\0",
                "H" => commit.Sha,
                "P" => string.Join(' ', commit.Parents.Select(p => p.Sha)),
                "an" => commit.Author.Name,
                "ae" => commit.Author.Email,
                "aI" => IsoStrict(commit.Author.When),
                "cn" => commit.Committer.Name,
                "ce" => commit.Committer.Email,
                "B" => commit.Message,
                "D" => decorations.TryGetValue(commit.Sha, out var d) ? string.Join(", ", d) : "",
                _ => m.Value
            })).Append('\n');
        }
        return Ok(output.ToString());
    }

    /// <summary>Reference names per commit as <c>git log %D</c> shows them: HEAD first, then branches, remote branches and tags.</summary>
    private static Dictionary<string, List<string>> Decorations(GitRepo repo)
    {
        var result = new Dictionary<string, List<string>>();
        void Add(string sha, string name) { if (!result.TryGetValue(sha, out var list)) result[sha] = list = []; list.Add(name); }
        var head = repo.Refs.Head as SymbolicReference;
        if (repo.Head.Tip is { } tip) Add(tip.Sha, head is null ? "HEAD" : "HEAD -> " + Short(head.TargetIdentifier));
        foreach (var r in repo.Refs.OrderBy(r => r.CanonicalName.StartsWith("refs/heads/", StringComparison.Ordinal) ? 0 : r.CanonicalName.StartsWith("refs/remotes/", StringComparison.Ordinal) ? 1 : 2).ThenBy(r => r.CanonicalName, StringComparer.Ordinal))
        {
            if (head is not null && r.CanonicalName == head.TargetIdentifier) continue;
            if (!r.CanonicalName.StartsWith("refs/heads/", StringComparison.Ordinal) && !r.CanonicalName.StartsWith("refs/remotes/", StringComparison.Ordinal) && !r.CanonicalName.StartsWith("refs/tags/", StringComparison.Ordinal)) continue;
            if (r.ResolveToDirectReference()?.Target.AsCommit() is not { } c) continue;
            Add(c.Sha, (r.CanonicalName.StartsWith("refs/tags/", StringComparison.Ordinal) ? "tag: " : "") + Short(r.CanonicalName));
        }
        return result;
    }

    private static GitResult RevList(GitRepo repo, List<string> args)
    {
        var leftRight = args.Contains("--left-right");
        var positive = new List<Commit>(); var negative = new List<Commit>(); var not = false; string? exclude = null;
        foreach (var a in args.Skip(1).Where(a => a is not "--count" and not "--left-right"))
        {
            if (a == "--not") { not = true; continue; }
            if (a.StartsWith("--exclude=", StringComparison.Ordinal)) { exclude = a["--exclude=".Length..]; continue; }
            if (a is "--branches" or "--remotes" || a.StartsWith("--glob=", StringComparison.Ordinal))
            {
                var prefix = a == "--branches" ? "refs/heads/" : a == "--remotes" ? "refs/remotes/" : a["--glob=".Length..];
                foreach (var r in repo.Refs.Where(r => prefix.Contains('*') ? Matches(r.CanonicalName, prefix) : r.CanonicalName.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    if (exclude is not null && a != "--remotes" && Short(r.CanonicalName) == exclude) continue;
                    if (r.ResolveToDirectReference()?.Target.AsCommit() is { } c) (not ? negative : positive).Add(c);
                }
                continue;
            }
            if (a.Contains("...", StringComparison.Ordinal))
            {
                var parts = a.Split("..."); var left = ResolveCommit(repo, parts[0])!; var right = ResolveCommit(repo, parts[1])!;
                var (ahead, behind) = AheadBehind(repo, left, right);
                return Ok(leftRight ? ahead + "\t" + behind + "\n" : (ahead + behind) + "\n");
            }
            if (a.Contains("..", StringComparison.Ordinal))
            {
                var parts = a.Split(".."); negative.Add(ResolveCommit(repo, parts[0])!); positive.Add(ResolveCommit(repo, parts[1])!); continue;
            }
            var commit = ResolveCommit(repo, a) ?? throw new LibGit2SharpException("bad revision '" + a + "'");
            (not ? negative : positive).Add(commit);
        }
        return Ok(Count(repo, positive, negative).ToString(CultureInfo.InvariantCulture) + "\n");
    }

    private static GitResult MergeBase(GitRepo repo, List<string> args)
    {
        var operands = Operands(args); var a = ResolveCommit(repo, operands[0]); var b = ResolveCommit(repo, operands[1]);
        if (a is null || b is null) return Fail("fatal: Not a valid object name");
        var baseCommit = repo.ObjectDatabase.FindMergeBase(a, b);
        if (args.Contains("--is-ancestor")) return baseCommit?.Sha == a.Sha ? Ok("") : Quiet();
        return baseCommit is null ? Quiet() : Ok(baseCommit.Sha + "\n");
    }

    // ---- switching and merging -----------------------------------------------------------------------------------

    private static GitResult Switch(GitRepo repo, List<string> args)
    {
        var operands = Operands(args);
        if (args.Contains("-c"))
        {
            var name = args[args.IndexOf("-c") + 1]; var start = args.Contains("--track") ? operands.Last() : null;
            var at = start is null ? repo.Head.Tip : ResolveCommit(repo, start);
            if (at is null) return Fail("fatal: invalid reference: " + start);
            var created = repo.Branches.Add(name, at);
            if (start is not null)
            {
                var remoteName = start.Split('/')[0];
                repo.Config.Set("branch." + name + ".remote", remoteName, ConfigurationLevel.Local); repo.Config.Set("branch." + name + ".merge", "refs/heads/" + start[(remoteName.Length + 1)..], ConfigurationLevel.Local);
            }
            return Checkout(repo, created);
        }
        var branch = repo.Branches[operands.Single()];
        return branch is null ? Fail("fatal: invalid reference: " + operands.Single()) : Checkout(repo, branch);
    }

    private static GitResult Checkout(GitRepo repo, LibGit2Sharp.Branch branch)
    {
        var blocked = new List<string>();
        try
        {
            Commands.Checkout(repo, branch, new CheckoutOptions { CheckoutNotifyFlags = CheckoutNotifyFlags.Conflict, OnCheckoutNotify = (path, _) => { blocked.Add(path); return true; } });
            return Ok("");
        }
        catch (CheckoutConflictException) { return Fail(Overwritten(blocked, "checkout"), 1); }
    }

    private static string Overwritten(IEnumerable<string> paths, string operation) =>
        "error: Your local changes to the following files would be overwritten by " + operation + ":\n" + string.Concat(paths.Select(p => "\t" + p + "\n")) + "Please commit your changes or stash them before you switch branches.\nAborting";

    private static GitResult Merge(GitRepo repo, List<string> args)
    {
        if (!args.Contains("--ff-only")) return Usage();
        var target = ResolveCommit(repo, Operands(args).Single());
        if (target is null) return Fail("merge: " + Operands(args).Single() + " - not something we can merge");
        var signature = repo.Config.BuildSignature(DateTimeOffset.Now) ?? new Signature("TravelRepo", "travelrepo@invalid", DateTimeOffset.Now);
        var blocked = new List<string>();
        try
        {
            var result = repo.Merge(target, signature, new MergeOptions { FastForwardStrategy = FastForwardStrategy.FastForwardOnly, CheckoutNotifyFlags = CheckoutNotifyFlags.Conflict, OnCheckoutNotify = (path, _) => { blocked.Add(path); return true; } });
            return result.Status is MergeStatus.FastForward or MergeStatus.UpToDate ? Ok("") : Fail("fatal: Not possible to fast-forward, aborting.");
        }
        catch (NonFastForwardException) { return Fail("fatal: Not possible to fast-forward, aborting."); }
        catch (CheckoutConflictException) { return Fail(Overwritten(blocked, "merge"), 1); }
    }

    private static GitResult Abort(GitRepo repo, string operation)
    {
        if (operation == "rebase") { repo.Rebase.Abort(); return Ok(""); }
        // Like "git merge --abort": back to the version before the operation; the operation's files are removed.
        repo.Reset(ResetMode.Hard);
        foreach (var marker in new[] { "MERGE_HEAD", "MERGE_MSG", "MERGE_MODE", "CHERRY_PICK_HEAD", "REVERT_HEAD", "AUTO_MERGE" })
            File.Delete(Path.Combine(repo.Info.Path, marker));
        return Ok("");
    }

    // ---- remotes -----------------------------------------------------------------------------------------------

    private static GitResult Remote(GitRepo repo, List<string> args)
    {
        if (args.Count == 1) return Ok(string.Concat(repo.Network.Remotes.Select(r => r.Name + "\n")));
        if (args[1] == "get-url") return repo.Network.Remotes[args[2]] is { } r ? Ok(r.Url + "\n") : Fail("error: No such remote '" + args[2] + "'", 2);
        if (args[1] == "add") { repo.Network.Remotes.Add(args[2], args[3]); return Ok(""); }
        return Usage();
    }

    private static CredentialsHandler? Credentials(GitCredential? credential) => credential is null ? null :
        (_, _, _) => new UsernamePasswordCredentials { Username = credential.Username, Password = credential.Password };

    /// <summary>A libgit2 transport error written the way Git reports it, so it is classified as network or sign-in trouble.</summary>
    private static string Transport(string url, LibGit2SharpException ex)
    {
        var text = ManagedHttpTransport.TakeFailure() ?? ex.Message;
        if (text.Contains("401", StringComparison.Ordinal) || text.Contains("403", StringComparison.Ordinal) || text.Contains("authentication", StringComparison.OrdinalIgnoreCase) || text.Contains("credentials", StringComparison.OrdinalIgnoreCase))
            return "fatal: Authentication failed for '" + url + "'\n" + text;
        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase) && text.Contains("repository", StringComparison.OrdinalIgnoreCase))
            return "fatal: repository '" + url + "' not found\nRepository not found\n" + text;
        return "fatal: unable to access '" + url + "': " + text;
    }

    private static GitResult Fetch(GitRepo repo, List<string> args, GitCredential? credential)
    {
        var name = Operands(args).Single(); var remote = repo.Network.Remotes[name];
        if (remote is null) return Fail("fatal: '" + name + "' does not appear to be a git repository");
        try
        {
            Commands.Fetch(repo, name, remote.FetchRefSpecs.Select(r => r.Specification), new FetchOptions { Prune = args.Contains("--prune"), CredentialsProvider = Credentials(credential) }, null);
            return Ok("");
        }
        catch (LibGit2SharpException ex) { return Fail(Transport(remote.Url, ex)); }
    }

    private static GitResult Push(GitRepo repo, List<string> args, GitCredential? credential)
    {
        var operands = Operands(args); var remote = repo.Network.Remotes[operands[0]];
        if (remote is null) return Fail("fatal: '" + operands[0] + "' does not appear to be a git repository");
        var specs = new List<string>();
        if (args.Contains("--delete")) specs.AddRange(operands.Skip(1).Select(b => ":refs/heads/" + b));
        else foreach (var spec in operands.Skip(1))
        {
            var colon = spec.IndexOf(':'); var source = spec[..colon];
            if (source == "HEAD") source = repo.Head.CanonicalName;
            specs.Add(source + spec[colon..]);
        }
        var rejected = new List<string>();
        try
        {
            repo.Network.Push(remote, specs, new PushOptions { CredentialsProvider = Credentials(credential), OnPushStatusError = e => rejected.Add(e.Reference + " (" + e.Message + ")") });
        }
        catch (NonFastForwardException) { return Fail(Rejected(remote.Url, specs), 1); }
        catch (LibGit2SharpException ex) { return Fail(Transport(remote.Url, ex)); }
        if (rejected.Count > 0) return Fail(" ! [remote rejected] " + string.Join("\n ! [remote rejected] ", rejected) + "\nerror: failed to push some refs to '" + remote.Url + "'", 1);
        if (args.Contains("--set-upstream"))
            foreach (var spec in specs)
            {
                var colon = spec.IndexOf(':'); var source = spec[..colon]; var destination = spec[(colon + 1)..];
                if (!source.StartsWith("refs/heads/", StringComparison.Ordinal)) continue;
                var branch = source["refs/heads/".Length..];
                repo.Config.Set("branch." + branch + ".remote", remote.Name, ConfigurationLevel.Local); repo.Config.Set("branch." + branch + ".merge", destination, ConfigurationLevel.Local);
            }
        return Ok("");
    }

    private static string Rejected(string url, IEnumerable<string> specs) =>
        string.Concat(specs.Select(s => " ! [rejected]        " + s + " (fetch first)\n")) + "error: failed to push some refs to '" + url + "'\nhint: Updates were rejected because the remote contains work that you do not have locally.";
}

internal static class GitObjects
{
    /// <summary>The commit an object stands for: itself, or the commit an annotated tag points to.</summary>
    public static Commit? AsCommit(this GitObject? value) => value switch { Commit c => c, TagAnnotation t => t.Target.AsCommit(), _ => null };
}
